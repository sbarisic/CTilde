using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CTilde;
using CTilde.Langs;
using CTilde.Expr;

namespace Test {
	class Program {
		static void Main(string[] Args) {
			if (Args.Length == 0)
				Args = new string[] { "tests/FishAsm.ct" };

			Console.Title = "C~ Test";

			Tokenizer Tokenizer = new Tokenizer(Args[0]);
			Parser Parser = new Parser(Tokenizer);

			LangProvider Lng = new FishAsmProvider();
			Lng.Compile(Parser.Parse());

			File.WriteAllText("out.asm", Lng.CompileToSource());
		}
	}
}
