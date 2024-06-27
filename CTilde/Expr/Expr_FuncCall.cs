using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr {
	public class Expr_FuncCall : Expression {
		public Expr_Identifier Function;
		public List<Expression> Arguments = new List<Expression>();

		public override Expression Parse(Tokenizer Tok) {
			Function = new Expr_Identifier().Parse<Expr_Identifier>(Tok);
			Tok.NextToken().Assert(Symbol.LParen);

			while (!Tok.Peek().Is(Symbol.RParen)) {
				Expression E = Expression.ParseExpression(Tok);
				Arguments.Add(E);

				if (!Tok.Peek().Is(Symbol.RParen)) {
					Tok.NextToken().Assert(Symbol.Comma);
				}
			}

			Tok.NextToken().Assert(Symbol.RParen);
			return this;
		}
	}
}
