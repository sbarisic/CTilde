using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr {
	public class ParamDefData {
		public Expr_TypeDef ParamType;
		public string Name;

		public ParamDefData(Expr_TypeDef ParamType, string Name) {
			this.ParamType = ParamType;
			this.Name = Name;
		}
	}

	public class Expr_ParamsDef : Expression {
		public List<ParamDefData> Definitions = new List<ParamDefData>();

		public Expr_ParamsDef Append(ParamDefData Dat) {
			Definitions.Add(Dat);
			return this;
		}

		public Expr_ParamsDef Prepend(ParamDefData Dat) {
			Definitions.Insert(0, Dat);
			return this;
		}

		public override Expression Parse(Tokenizer Tok) {
			while (!Tok.Peek().IsSymbol(Symbol.RParen)) {
				ParamDefData Def = new ParamDefData(new Expr_TypeDef().Parse<Expr_TypeDef>(Tok), Tok.NextToken().Assert(TokenType.Identifier).Text);

				Append(Def);

				if (!Tok.Peek().IsSymbol(Symbol.RParen))
					Tok.NextToken().Assert(Symbol.Comma);
			}

			return this;
		}
	}
}
