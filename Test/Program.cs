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
			Main2();
			return;


			if (Args.Length == 0)
				Args = new string[] { "tests/Test.ct" };

			Console.Title = "C~ Test";

			Tokenizer Tokenizer = new Tokenizer(Args[0]);
			Parser Parser = new Parser(Tokenizer);

			LangProvider Lng = new CLangProvider();
			Lng.Compile(Parser.Parse());

			File.WriteAllText("out.c", Lng.CompileToSource());
		}

		static void Main2() {
			while (true) {
				Console.Write(">> ");
				string In = Console.ReadLine().Trim();

				int[] Numbers = In.Select(C => int.Parse(C.ToString())).ToArray();

				Console.WriteLine("Numbers: {0}; Checksum: {1}", string.Join("", Numbers.Select(N => N.ToString())), ChecksumMod(Numbers));
			}
		}

		static int ChecksumMod(int[] Numbers) {
			int Last = 10;

			for (int i = 0; i < Numbers.Length; i++) {
				int N = Numbers[i];

				if (N < 0 || N > 9)
					throw new Exception("Expected single digit number [0-9]");

				int Temp = (N + Last) % 10;

				if (Temp == 0)
					Temp = 10;

				Last = (Temp * 2) % 11;
			}

			int Checksum = 11 - Last;
			return Checksum;
		}
	}
}
