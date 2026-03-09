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
        public static extern void td_json_client_send(IntPtr client, [MarshalAs(UnmanagedType.LPStr)] string request);

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr td_json_client_receive(IntPtr client, double timeout);

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void td_json_client_destroy(IntPtr client);

        public static void SendUtf8(IntPtr client, string request)
        {
            td_json_client_send(client, request);
        }

        public static string IntPtrToStringUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return Encoding.UTF8.GetString(buffer);
        }
    }
}
