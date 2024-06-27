using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr {
	public class Expr_Identifier : Expression {
		public string Identifier;

		public Expr_Identifier() {
		}

		public Expr_Identifier(string Identifier) {
			this.Identifier = Identifier;
		}

		public override Expression Parse(Tokenizer Tok) {
			Identifier = Tok.NextToken().Assert(TokenType.Identifier).Text;
			return this;
		}
	}
}
