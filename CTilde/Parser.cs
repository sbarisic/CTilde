using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde {
	public class Parser {
		Tokenizer Tokenizer;

		public Parser(Tokenizer Tokenizer) {
			this.Tokenizer = Tokenizer;
		}

		public Expression Parse() {
			return new Expr_Module().Parse(Tokenizer);
		}
	}
}
