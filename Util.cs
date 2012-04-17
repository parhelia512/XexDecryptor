using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace XexDecryptor {
    public static class Util {
        public static byte[] ReadEntireStream (Stream stream) {
            var result = new List<byte>();
            var buffer = new byte[32767];

            while (true) {
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead < buffer.Length) {

                    if (bytesRead > 0) {
                        result.Capacity = result.Count + bytesRead;
                        result.AddRange(buffer.Take(bytesRead));
                    }

                    if (bytesRead <= 0)
                        break;
                } else {
                    result.AddRange(buffer);
                }
            }

            return result.ToArray();
        }

        public static void RunProcess (string filename, string parameters, byte[] stdin, out string stderr, out byte[] stdout) {
            var psi = new ProcessStartInfo(filename, parameters);

            psi.WorkingDirectory = Path.GetDirectoryName(filename);
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;

            using (var process = Process.Start(psi)) {
                var stdinStream = process.StandardInput.BaseStream;
                var stderrStream = process.StandardError.BaseStream;

                if (stdin != null) {
                    ThreadPool.QueueUserWorkItem(
                        (_) => {
                            if (stdin != null) {
                                stdinStream.Write(
                                    stdin, 0, stdin.Length
                                );
                                stdinStream.Flush();
                            }

                            stdinStream.Close();
                        }, null
                    );
                }

                var temp = new string[1] { null };
                ThreadPool.QueueUserWorkItem(
                    (_) => {
                        var text = Encoding.ASCII.GetString(ReadEntireStream(stderrStream));
                        temp[0] = text;
                    }, null
                );

                stdout = ReadEntireStream(process.StandardOutput.BaseStream);

                process.WaitForExit();
                stderr = temp[0];

                process.Close();
            }
        }
    }
}
