using System;
using System.IO;
using System.Text;
using TelnetNegotiationCore.Machine;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var output = Path.Combine(root, "docs", "generated");
Directory.CreateDirectory(output);
File.WriteAllText(Path.Combine(output, "telnet-core-machine.mmd"), TelnetCoreMachine.Mermaid + "\n", new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "telnet-core-machine.dot"), TelnetCoreMachine.Dot + "\n", new UTF8Encoding(false));
