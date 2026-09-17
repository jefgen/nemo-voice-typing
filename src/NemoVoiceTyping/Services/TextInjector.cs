using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace NemoVoiceTyping.Services;

/// <summary>
/// Injects text into whichever window currently has keyboard focus.
///
/// Prefers synthesised keystrokes (SendInput with KEYEVENTF_UNICODE, which
/// covers any Unicode codepoint including supplementary planes via UTF-16
/// surrogate pairs) and leaves the clipboard alone. Some targets drop or
/// scramble injected input events, so for those the text is pasted instead;
/// see <see cref="ClassLosesKeystrokes"/>.
/// </summary>
public static class TextInjector
{
    /// <summary>
    /// Injection runs on one dedicated background thread.
    ///
    /// Callers (the ASR worker, the tick timer, the UI thread) must never block:
    /// pacing injection on the ASR thread stalls speech processing, which backs
    /// up the audio queue and makes recognition both slower and burstier. The
    /// queue also serialises every keystroke into a single thread, so ordering
    /// between typing and backspaces is preserved.
    /// </summary>
    private static readonly BlockingCollection<Action> Pending = new();

    static TextInjector()
    {
        var pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "Text Injector",
        };
        pump.SetApartmentState(ApartmentState.STA);
        pump.Start();
    }

    /// <summary>
    /// Minimum quiet period after each injected item, in milliseconds. Keeps
    /// consecutive pastes from overlapping while the target processes Ctrl+V.
    /// </summary>
    private const int QuietPeriodMs = 25;

    private static void Pump()
    {
        foreach (var work in Pending.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { Debug.WriteLine(ex); }
            Thread.Sleep(QuietPeriodMs);
        }
    }

    /// <summary>
    /// Blocks until everything queued so far has been injected. The signal is
    /// deliberately not disposed: on timeout this returns while the queued
    /// action is still pending, and the injector thread would then set a
    /// disposed handle.
    /// </summary>
    public static void Flush(TimeSpan timeout)
    {
        var done = new ManualResetEventSlim(false);
        Pending.Add(done.Set);
        done.Wait(timeout);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_BACK = 0x08;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int count);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>
    /// Inserts text by putting it on the clipboard and sending Ctrl+V.
    ///
    /// Synthesised keystrokes are unreliable against WinUI 3 targets such as
    /// Windows 11's Notepad, which drops and mangles injected input events —
    /// real virtual-key codes fare no better than Unicode packets, so the loss
    /// is in input delivery rather than character encoding. A paste sends four
    /// events regardless of the text's length and carries the payload outside
    /// the input queue entirely, which measured clean where keystrokes did not.
    /// Falls back to keystrokes if the clipboard cannot be opened.
    /// </summary>
    public static void Type(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Pending.Add(() => TypeCore(text));
    }

    private static void TypeCore(string text)
    {
        if (NeedsClipboard() && TrySetClipboard(text))
        {
            Send(Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true));
            return;
        }

        foreach (var unit in text)
            Send(Packet(unit, up: false), Packet(unit, up: true));
    }

    /// <summary>Set from <c>AppConfig.TextInjection</c>: auto, keystrokes or clipboard.</summary>
    public static string Mode { get; set; } = "auto";

    /// <summary>
    /// Focused-control classes that lose synthesised keystrokes. "InputSite"
    /// matches the XAML input-host family (<c>Windows.UI.Input.InputSite.
    /// WindowClass</c>, <c>InputSiteWindowClass</c>) used by Terminal, File
    /// Explorer and the shell; <c>RichEditD2DPT</c> is Notepad's editor.
    /// </summary>
    private static bool ClassLosesKeystrokes(string className) =>
        className.Contains("InputSite", StringComparison.OrdinalIgnoreCase)
        || className.Equals("RichEditD2DPT", StringComparison.OrdinalIgnoreCase);

    private static IntPtr _lastForeground;
    private static bool _lastDecision;

    /// <summary>
    /// Decides per foreground window, caching the answer so the thread-attach
    /// dance below runs on window changes rather than on every word.
    /// </summary>
    private static bool NeedsClipboard()
    {
        if (Mode.Equals("clipboard", StringComparison.OrdinalIgnoreCase)) return true;
        if (Mode.Equals("keystrokes", StringComparison.OrdinalIgnoreCase)) return false;

        IntPtr foreground = GetForegroundWindow();
        if (foreground == _lastForeground) return _lastDecision;

        _lastForeground = foreground;
        _lastDecision = ClassLosesKeystrokes(FocusedClassName(foreground));
        return _lastDecision;
    }

    /// <summary>
    /// Reads the class of the control with keyboard focus inside another
    /// process, which requires briefly sharing its input queue.
    /// </summary>
    private static string FocusedClassName(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return "";

        uint self = GetCurrentThreadId();
        uint owner = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        if (!AttachThreadInput(self, owner, true)) return "";

        try
        {
            IntPtr focused = GetFocus();
            if (focused == IntPtr.Zero) return "";
            var name = new StringBuilder(256);
            GetClassNameW(focused, name, name.Capacity);
            return name.ToString();
        }
        finally
        {
            AttachThreadInput(self, owner, false);
        }
    }

    /// <summary>
    /// Publishes text with the opt-out formats that keep dictated words out of
    /// clipboard history. The first three are Windows' own; "Clipboard Viewer
    /// Ignore" is the long-standing convention third-party managers such as
    /// Ditto and ClipMate check, since the Windows formats do not bind them.
    /// All of them are advisory — a manager that ignores them will still see
    /// the text.
    /// </summary>
    private static bool TrySetClipboard(string text)
    {
        CaptureOriginalClipboard();

        var data = new DataObject();
        data.SetText(text);
        foreach (var format in new[]
                 {
                     "ExcludeClipboardContentFromMonitorProcessing",
                     "CanIncludeInClipboardHistory",
                     "CanUploadToCloudClipboard",
                     "Clipboard Viewer Ignore",
                 })
        {
            data.SetData(format, new MemoryStream(new byte[] { 0, 0, 0, 0 }));
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                Thread.Sleep(20);
            }
        }
        return false;
    }

    private static string? _originalClipboard;
    private static bool _clipboardCaptured;

    private static void CaptureOriginalClipboard()
    {
        if (_clipboardCaptured) return;
        _clipboardCaptured = true;
        try { _originalClipboard = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    /// <summary>
    /// Puts back whatever the user had copied before dictation started. Queued
    /// so it runs after the last paste, and done once per session rather than
    /// after every word.
    /// </summary>
    public static void RestoreClipboard()
    {
        Pending.Add(() =>
        {
            if (!_clipboardCaptured) return;
            _clipboardCaptured = false;
            try
            {
                if (_originalClipboard != null) Clipboard.SetText(_originalClipboard);
                else Clipboard.Clear();
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            _originalClipboard = null;
        });
    }

    private static INPUT Key(ushort virtualKey, bool up) => new INPUT
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = virtualKey, dwFlags = up ? KEYEVENTF_KEYUP : 0 } }
    };

    private static INPUT Packet(char unit, bool up) => new INPUT
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wScan = unit, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) }
        }
    };

    private static void Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));

    public static void Backspace(int count)
    {
        if (count <= 0) return;
        Pending.Add(() => BackspaceCore(count));
    }

    private static void BackspaceCore(int count)
    {
        var pair = new INPUT[2];
        for (int i = 0; i < count; i++)
        {
            pair[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_BACK, wScan = 0, dwFlags = 0 } }
            };
            pair[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_BACK, wScan = 0, dwFlags = KEYEVENTF_KEYUP } }
            };
            SendInput(2, pair, Marshal.SizeOf(typeof(INPUT)));
        }
    }

    public static void PressEnter() => Pending.Add(PressEnterCore);

    private static void PressEnterCore()
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_RETURN } } };
        inputs[1] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_RETURN, dwFlags = KEYEVENTF_KEYUP } } };
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }
}