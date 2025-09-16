// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Community.PowerToys.Run.Plugin.VSCodeWorkspaces.SshConfigParser
{
    public class SshConfig
    {
        public static IEnumerable<SshHost> ParseFile(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        public static IEnumerable<SshHost> Parse(string str)
        {
            var list = new List<SshHost>();

            using (var reader = new StringReader(str))
            {
                string? line;
                SshHost? current = null;

                while ((line = reader.ReadLine()) != null)
                {
                    // Normalize and ignore comments/empty lines
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("#"))
                        continue;

                    // Split into key and value (value may contain spaces)
                    var parts = trimmed.Split(new[] { ' ' }, 2, System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                        continue;

                    var key = parts[0];
                    var value = parts.Length > 1 ? parts[1].Trim() : string.Empty;

                    if (key.Equals("Host", System.StringComparison.OrdinalIgnoreCase))
                    {
                        // Start of a new host block. Add previous if exists.
                        if (current != null)
                        {
                            list.Add(current);
                        }

                        current = new SshHost();
                        // The Host directive can contain multiple patterns; keep as-is
                        current["Host"] = value;
                        current.Host = value;
                    }
                    else if (current != null)
                    {
                        // Other directives are stored on the current host
                        current[key] = value;
                    }
                }

                if (current != null)
                {
                    list.Add(current);
                }
            }

            return list;
        }
    }
}
