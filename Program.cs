using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XexDecryptor {
    public static class Program {
        public static void Abort (string format, params object[] args) {
            Console.Error.WriteLine(format, args);
            Environment.Exit(1);
        }

        public static int? FindByteString (byte[] bytes, int startOffset, int? limit, byte[] searchString) {
            int i = startOffset;
            int end = limit.GetValueOrDefault(bytes.Length - i) + i;

            int matchStart = 0, matchPos = 0;

            while (i < end) {
                var current = bytes[i];

                if (current != searchString[matchPos]) {
                    if (matchPos != 0) {
                        matchPos = 0;
                        continue;
                    }
                } else {
                    if (matchPos == 0)
                        matchStart = i;

                    matchPos += 1;
                    if (matchPos == searchString.Length)
                        return matchStart;
                }

                i++;
            }

            return 0;
        }

        public static int? FindExecutableHeader (byte[] bytes, int startOffset) {
            int? offset = FindByteString(bytes, startOffset, null, new byte[] { 0x4D, 0x5A });
            if (!offset.HasValue)
                return null;

            int? offset2 = FindByteString(bytes, offset.Value + 2, 512, new byte[] { 0x50, 0x45, 0x00, 0x00 });
            if (!offset2.HasValue)
                return null;

            return offset;
        }

        public static void Main (string[] args) {
            if (args.Length < 1) {
                Abort("Usage: XexDecryptor [xex filenames]");
            }

            var filenames = new List<string>();
            foreach (var filename in args) {
                if (filename.IndexOfAny(new char[] { '*', '?' }) < 0) {
                    filenames.Add(filename);
                } else {
                    var dirname = Path.GetDirectoryName(filename);
                    if (String.IsNullOrWhiteSpace(dirname))
                        dirname = Environment.CurrentDirectory;

                    filenames.AddRange(Directory.GetFiles(dirname, Path.GetFileName(filename)));
                }
            }

            foreach (var sourceFile in filenames) {
                Console.WriteLine("Decrypting {0}...", sourceFile);

                if (!File.Exists(sourceFile)) {
                    Abort("File not found: {0}", sourceFile);
                }

                string outputFile = Path.GetFullPath(sourceFile);
                if (outputFile.ToLower().EndsWith(".xex")) {
                    outputFile = Path.Combine(
                        Path.GetDirectoryName(outputFile),
                        Path.GetFileNameWithoutExtension(outputFile)
                    );
                } else {
                    outputFile += ".decrypted";
                }

                var tempFilePath = Path.GetTempFileName();
                File.Delete(tempFilePath);

                string stdError;
                byte[] stdOut;

                Util.RunProcess(
                    "xextool.exe",
                    String.Format(
                        "-c u -e u -o \"{0}\" \"{1}\"",
                        tempFilePath,
                        sourceFile
                    ),
                    null, out stdError, out stdOut
                );

                if (!String.IsNullOrWhiteSpace(stdError)) {
                    File.Delete(tempFilePath);
                    Abort("XexTool reported error: {0}", stdError);
                }

                var xexBytes = File.ReadAllBytes(tempFilePath);
                File.Delete(tempFilePath);

                var firstHeader = FindExecutableHeader(xexBytes, 0);
                if (!firstHeader.HasValue) {
                    Abort("File is not a valid executable.");
                }

                var secondHeader = FindExecutableHeader(xexBytes, firstHeader.Value + 128);
                if (!secondHeader.HasValue) {
                    Abort("File does not contain an embedded executable.");
                }

                Console.Write("Extracting to '{0}'... ", outputFile);
                using (var fs = File.OpenWrite(outputFile)) {
                    fs.Write(xexBytes, secondHeader.Value, xexBytes.Length - secondHeader.Value);
                }
                Console.WriteLine("done.");

                Console.WriteLine("Rewriting assembly... ");
                AssemblyRewriter.Rewrite(outputFile);
            }

            Console.WriteLine("{0} assemblies processed.", filenames.Count);
        }
    }
}
