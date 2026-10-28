using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NLog;

namespace FontsTry;

public static class KeyboardHook
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private static LowLevelKeyboardProc _proc = HookCallback;
    private static IntPtr _hookID = IntPtr.Zero;

    // Ostatnio zalogowana aplikacja docelowa — logujemy ją tylko przy zmianie
    private static string? _lastAppName;
    private static string? _lastWindowName;

    private static KeyboardEvents _events = KeyboardEvents.All;

    // Blokuje wątek do anulowania tokena. Musi być wywołane na dedykowanym wątku STA,
    // bo hook LL wymaga pętli komunikatów na wątku, który go zainstalował.
    public static void Run(KeyboardEvents events, CancellationToken cancellationToken)
    {
        _events = events;
        Logger.Info($"[KEYBOARD DEBUG] Inicjalizacja hooka systemowego z wykrywaniem aplikacji... Zdarzenia: {events}");
        Logger.Debug($"System: {Environment.OSVersion}, 64bit: {Environment.Is64BitOperatingSystem}, Sesja: {Process.GetCurrentProcess().SessionId}");

        // Ukryta kontrolka tworzy kolejkę komunikatów, przez którą zatrzymujemy pętlę z innego wątku
        using var messageWindow = new Control();
        messageWindow.CreateControl();
        _ = messageWindow.Handle;

        _hookID = SetHook(_proc);
        if (_hookID == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Nie udało się zainstalować hooka klawiatury");
        }

        try
        {
            using var registration = cancellationToken.Register(() => messageWindow.BeginInvoke(Application.ExitThread));
            if (!cancellationToken.IsCancellationRequested)
            {
                Application.Run();
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hookID);
            _hookID = IntPtr.Zero;
            Logger.Info("[KEYBOARD DEBUG] Hook odinstalowany.");
        }
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using (Process curProcess = Process.GetCurrentProcess())
        using (ProcessModule curModule = curProcess.MainModule!)
        {
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            int scanCode = Marshal.ReadInt32(lParam + 4);
            int flags = Marshal.ReadInt32(lParam + 8);

            KeyboardEvents keyboardEvent = KeyboardEvents.None;
            int msg = wParam.ToInt32();

            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) keyboardEvent = KeyboardEvents.KeyDown;
            if (msg == WM_KEYUP || msg == WM_SYSKEYUP) keyboardEvent = KeyboardEvents.KeyUp;

            if (keyboardEvent != KeyboardEvents.None && _events.HasFlag(keyboardEvent))
            {
                string eventType = keyboardEvent.ToString().ToUpperInvariant();

                // 1. Pobranie informacji o aktywnej aplikacji
                string activeAppName = "Unknown";
                string activeWindowName = "Unknown";
                try
                {
                    IntPtr hwnd = GetForegroundWindow();
                    if (hwnd != IntPtr.Zero)
                    {
                        // Pobranie tytułu okna
                        int textLength = GetWindowTextLength(hwnd);
                        if (textLength > 0)
                        {
                            StringBuilder windowTitle = new StringBuilder(textLength + 1);
                            GetWindowText(hwnd, windowTitle, windowTitle.Capacity);
                            activeWindowName = windowTitle.ToString();
                        }

                        // Pobranie nazwy procesu (np. chrome, notepad)
                        GetWindowThreadProcessId(hwnd, out uint processId);
                        if (processId != 0)
                        {
                            using (Process proc = Process.GetProcessById((int)processId))
                            {
                                activeAppName = proc.ProcessName;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignorujemy błędy pobierania procesu (np. brak uprawnień do procesów systemowych)
                }

                // 2. Tłumaczenie znaków klawiatury
                byte[] keyState = new byte[256];
                GetKeyboardState(keyState);

                if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift) keyState[(int)Keys.ShiftKey] = 0x80;
                if ((Control.ModifierKeys & Keys.Control) == Keys.Control) keyState[(int)Keys.ControlKey] = 0x80;
                if ((Control.ModifierKeys & Keys.Alt) == Keys.Alt) keyState[(int)Keys.Menu] = 0x80;

                StringBuilder sbResult = new StringBuilder(10);
                int conversionResult = ToUnicode((uint)vkCode, (uint)scanCode, keyState, sbResult, sbResult.Capacity, (uint)flags);

                string translatedChar = "";
                if (conversionResult > 0)
                {
                    translatedChar = sbResult.ToString();
                }
                else if (conversionResult == -1)
                {
                    translatedChar = "[Dead Key]";
                }

                string charRepresentation = translatedChar;
                if (translatedChar.Length == 1 && char.IsControl(translatedChar[0]))
                {
                    charRepresentation = $"[CONTROL: 0x{(int)translatedChar[0]:X2}]";
                }
                else if (translatedChar == " ")
                {
                    charRepresentation = "[SPACE]";
                }

                // Informacja o aplikacji docelowej tylko wtedy, gdy się zmieniła
                if (activeAppName != _lastAppName || activeWindowName != _lastWindowName)
                {
                    _lastAppName = activeAppName;
                    _lastWindowName = activeWindowName;
                    Logger.Debug($"[APP] {activeAppName} (Window: \"{activeWindowName}\")");
                }

                Logger.Debug($"[{eventType}] " +
                             $"Key: {(Keys)vkCode} (VK: {vkCode}), " +
                             $"ScanCode: {scanCode}, " +
                             $"Char: '{charRepresentation}', " +
                             $"Modifiers: {Control.ModifierKeys}");
            }
        }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    #region Win32 API Imports

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    #endregion
}
