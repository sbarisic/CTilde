using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr {
	public class Expr_Identifier : Expression {
		public string Identifier;
		bool Assigned = false;

		public Expr_Identifier() {
			Assigned = false;
		}

		public Expr_Identifier(string Identifier) {
			this.Identifier = Identifier;
			Assigned = true;
		}

		public override Expression Parse(Tokenizer Tok) {
			if (Assigned)
				throw new InvalidOperationException("Can not call Parse after using non-default constructor");

			Identifier = Tok.NextToken().Assert(TokenType.Identifier).Text;
			return this;
		}
	}
}
