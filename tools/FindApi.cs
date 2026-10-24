using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

// Focused search across a Cecil API dump produced by DumpApi2.
// usage: FindApi <dumpFile> <regex> [<regex> ...]
// Prints: matching type headers AND, for those types, every member line whose text matches any regex.
internal static class FindApi
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: FindApi <dumpFile> <pattern> [pattern...]");
            return 2;
        }

        string dumpPath = args[0];
        var patterns = args.Skip(1).Select(p => new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();

        var lines = File.ReadAllLines(dumpPath);
        string currentType = null;
        var buf = new List<string>();
        var headerMatches = new List<string>();
        var memberMatches = new List<string>();
        var seenTypes = new HashSet<string>();

        void Flush()
        {
            bool headerHit = currentType != null && patterns.Any(p => p.IsMatch(currentType));
            bool memberHit = buf.Any(l => patterns.Any(p => p.IsMatch(l)));
            if (headerHit || memberHit)
            {
                Console.Out.WriteLine(currentType);
                if (headerHit && !memberHit)
                {
                    foreach (string l in buf) Console.Out.WriteLine(l);
                }
                else
                {
                    foreach (string l in buf.Where(l => patterns.Any(p => p.IsMatch(l)))) Console.Out.WriteLine(l);
                }
                Console.Out.WriteLine();
            }
            buf.Clear();
        }

        foreach (string line in lines)
        {
            bool isHeader = line.Length > 0 && !char.IsWhiteSpace(line[0]);
            if (isHeader)
            {
                Flush();
                currentType = line;
            }
            else if (line.Trim().Length > 0)
            {
                buf.Add(line.TrimEnd());
            }
        }
        Flush();

        return 0;
    }
}
