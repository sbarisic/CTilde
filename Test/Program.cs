using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CTilde;

namespace Test {
	class Program {
		static void Main(string[] Args) {
			Console.Title = "C~ Test";

			Tokenizer Tokenizer = new Tokenizer(Args[0]);
			Parser Parser = new Parser(Tokenizer);

			//try {
				Console.WriteLine(Parser.Compile());
			/*} catch (Exception E) {
				Console.WriteLine(E.Message);
			}*/
		
			Console.WriteLine("Done!");
			Console.ReadLine();
		}
	}
}
