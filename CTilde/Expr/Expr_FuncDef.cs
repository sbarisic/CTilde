using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr {
	public class Expr_FuncDef : Expression {
		public Expr_VariableDef FuncVariableDef;
		public Expr_ParamsDef FuncParams;
		public Expr_Block FuncBody;
		public bool IsCtor, IsDtor;

		public Expr_FuncDef() {
		}

		public override Expression Parse(Tokenizer Tok) {
			IsCtor = IsDtor = false;
			if (Tok.Peek().Is(Keyword.__ctor))
				IsCtor = true;
			else if (Tok.Peek().Is(Keyword.__dtor))
				IsDtor = true;

			if (IsCtor || IsDtor) {
				Tok.NextToken();
				FuncVariableDef = new Expr_VariableDef();
				FuncVariableDef.Name = Expr_ClassDef.CurrentClass.Name;
				FuncVariableDef.Type = Expr_TypeDef.MakeVoid();
			} else
				FuncVariableDef = new Expr_VariableDef().Parse<Expr_VariableDef>(Tok);

			if (IsCtor)
				FuncVariableDef.Name += "__ctor";
			else if (IsDtor)
				FuncVariableDef.Name += "__dtor";

			FuncParams = new Expr_ParamsDef().Parse<Expr_ParamsDef>(Tok);
			FuncBody = new Expr_Block().Parse<Expr_Block>(Tok);
			return this;
		}
	}
}
