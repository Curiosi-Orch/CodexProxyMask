using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexProxyMask
{
    // Captures an Enter submission and remembers a recent target-window click.
    // A click is only a candidate: MainForm also requires a new target proxy
    // connection before treating it as a submit-button action.
    internal sealed class SubmissionMonitor : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WhMouseLl = 14;
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;
        private const int WmLeftButtonUp = 0x0202;
        private const uint VkReturn = 0x0D;
        private const int VkShift = 0x10;
        private const uint LlkhfInjected = 0x00000010;
        private const uint LlmhfInjected = 0x00000001;

        private readonly LowLevelHookProc _keyboardHookProcedure;
        private readonly LowLevelHookProc _mouseHookProcedure;
        private readonly object _sync = new object();
        private HashSet<int> _targetProcessIds = new HashSet<int>();
        private int _pendingSubmissions;
        private DateTime _lastAcceptedSubmissionUtc = DateTime.MinValue;
        private DateTime _lastTargetClickUtc = DateTime.MinValue;
        private IntPtr _keyboardHookHandle;
        private IntPtr _mouseHookHandle;
        private bool _disposed;

        public SubmissionMonitor()
        {
            _keyboardHookProcedure = KeyboardHookCallback;
            _mouseHookProcedure = MouseHookCallback;
            try
            {
                using (var process = Process.GetCurrentProcess())
                using (var module = process.MainModule)
                {
                    var moduleHandle = GetModuleHandle(module == null ? null : module.ModuleName);
                    _keyboardHookHandle = SetWindowsHookEx(
                        WhKeyboardLl, _keyboardHookProcedure, moduleHandle, 0);
                    _mouseHookHandle = SetWindowsHookEx(
                        WhMouseLl, _mouseHookProcedure, moduleHandle, 0);
                }
            }
            catch
            {
                _keyboardHookHandle = IntPtr.Zero;
                _mouseHookHandle = IntPtr.Zero;
            }
        }

        public void UpdateTargetProcessIds(ISet<int> processIds)
        {
            lock (_sync)
            {
                _targetProcessIds = processIds == null
                    ? new HashSet<int>()
                    : new HashSet<int>(processIds);
            }
        }

        public bool TryConsumeSubmission()
        {
            lock (_sync)
            {
                if (_pendingSubmissions <= 0)
                {
                    return false;
                }

                _pendingSubmissions--;
                return true;
            }
        }

        public bool HasRecentTargetClick(TimeSpan maximumAge)
        {
            lock (_sync)
            {
                if (_lastTargetClickUtc == DateTime.MinValue)
                {
                    return false;
                }

                var age = DateTime.UtcNow - _lastTargetClickUtc;
                return age >= TimeSpan.Zero && age <= maximumAge;
            }
        }

        public void ConsumeRecentTargetClick()
        {
            lock (_sync)
            {
                _lastTargetClickUtc = DateTime.MinValue;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_keyboardHookHandle != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_keyboardHookHandle); } catch { }
                _keyboardHookHandle = IntPtr.Zero;
            }
            if (_mouseHookHandle != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_mouseHookHandle); } catch { }
                _mouseHookHandle = IntPtr.Zero;
            }
        }

        private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0
                    && (wParam == new IntPtr(WmKeyDown) || wParam == new IntPtr(WmSysKeyDown)))
                {
                    var keyboard = (KeyboardHookData)Marshal.PtrToStructure(
                        lParam, typeof(KeyboardHookData));
                    if (keyboard.VirtualKeyCode == VkReturn
                        && (keyboard.Flags & LlkhfInjected) == 0
                        && (GetAsyncKeyState(VkShift) & unchecked((short)0x8000)) == 0
                        && IsTargetWindowForeground())
                    {
                        lock (_sync)
                        {
                            var now = DateTime.UtcNow;
                            if (now - _lastAcceptedSubmissionUtc >= TimeSpan.FromMilliseconds(500))
                            {
                                _pendingSubmissions = Math.Min(_pendingSubmissions + 1, 4);
                                _lastAcceptedSubmissionUtc = now;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Input monitoring must never interfere with normal keyboard input.
            }

            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && wParam == new IntPtr(WmLeftButtonUp))
                {
                    var mouse = (MouseHookData)Marshal.PtrToStructure(
                        lParam, typeof(MouseHookData));
                    if ((mouse.Flags & LlmhfInjected) == 0 && IsTargetWindowForeground())
                    {
                        lock (_sync)
                        {
                            _lastTargetClickUtc = DateTime.UtcNow;
                        }
                    }
                }
            }
            catch
            {
                // Input monitoring must never interfere with normal mouse input.
            }

            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private bool IsTargetWindowForeground()
        {
            try
            {
                var window = GetForegroundWindow();
                uint processId;
                GetWindowThreadProcessId(window, out processId);
                lock (_sync)
                {
                    return processId != 0 && _targetProcessIds.Contains((int)processId);
                }
            }
            catch
            {
                return false;
            }
        }

        private delegate IntPtr LowLevelHookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardHookData
        {
            public uint VirtualKeyCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PointData
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseHookData
        {
            public PointData Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int hookType, LowLevelHookProc callback, IntPtr moduleHandle, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hookHandle, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKeyCode);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr windowHandle, out uint processId);
    }
}
