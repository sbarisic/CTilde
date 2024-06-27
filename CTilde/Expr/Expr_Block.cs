using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr {
	public class Expr_Block : Expression {
		public List<Expression> Expressions;

		public Expr_Block() {
			Expressions = new List<Expression>();
		}

		public override Expression Parse(Tokenizer Tok) {
			Tok.NextToken().Assert(Symbol.LBrace);

			while (!Tok.Peek().Is(Symbol.RBrace))
				Expressions.Add(Expression.ParseStatement(Tok));

			Tok.NextToken().Assert(Symbol.RBrace);
			return this;
		}
	}
}
