using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TelegramWP10
{
    public static class TdJson
    {
        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr td_json_client_create();

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void td_json_client_send(IntPtr client, IntPtr request);

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr td_json_client_receive(IntPtr client, double timeout);

        public static void SendUtf8(IntPtr client, string request)
        {
            if (string.IsNullOrEmpty(request)) return;
            // Кодируем в UTF-8 с нулевым терминатором для C++
            byte[] bytes = Encoding.UTF8.GetBytes(request + "\0");
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            try {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                td_json_client_send(client, ptr);
            } finally {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public static string IntPtrToStringUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return string.Empty;
            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return Encoding.UTF8.GetString(buffer);
        }
    }
}
