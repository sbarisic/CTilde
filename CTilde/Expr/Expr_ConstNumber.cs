using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr {
	public class Expr_ConstNumber : Expression {
		public string NumberLiteral;

		public override Expression Parse(Tokenizer Tok) {
			Token T = Tok.NextToken();

			if (T.Is(TokenType.Number) || T.Is(TokenType.Decimal))
				NumberLiteral = T.Text;
			else
				throw new NotImplementedException("Expected number or decimal");

			return this;
		}
	}
}
