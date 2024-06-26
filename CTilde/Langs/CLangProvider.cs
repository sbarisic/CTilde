using CTilde.Expr;

using System;
using System.CodeDom;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Langs {
	public class CLangProvider : LangProvider {
		bool OmmitSemicolon = false;

		public override void Compile(Expression Ex) {
			if (Ex == null)
				throw new ArgumentNullException(nameof(Ex));

			switch (Ex) {
				case Expr_Block Block: {
					AppendLine("{");

					foreach (var E in Block.Expressions) {
						Compile(E);
					}

					AppendLine("}");
					break;
				}

				case Expr_ClassDef ClassDef: {
					AppendLine("typedef struct {");

					foreach (var E in ClassDef.Variables) {
						Compile(E);
					}

					AppendLine("}} {0};", ClassDef.Name);

					foreach (var F in ClassDef.Functions) {
						Compile(F);
					}
					break;
				}

				case Expr_FuncDef FuncDef: {
					OmmitSemicolon = true;
					Compile(FuncDef.FuncVariableDef);

					Append("(");

					Compile(FuncDef.FuncParams);

					Append(")");
					OmmitSemicolon = false;

					Compile(FuncDef.FuncBody);
					break;
				}

				case Expr_Module Module: {
					foreach (var E in Module.Expressions)
						Compile(E);

					break;
				}

				case Expr_ParamsDef ParamsDef: {
					for (int i = 0; i < ParamsDef.Definitions.Count; i++) {
						Expr_VariableDef VarDef = ParamsDef.Definitions[i];
						Compile(VarDef);

						if (i + 1 < ParamsDef.Definitions.Count)
							Append(", ");
					}

					break;
				}

				case Expr_TypeDef TypeDef: {
					string T = TypeDef.Type;

					if (TypeDef.IsPointer)
						T += "*";
					else if (TypeDef.IsArray)
						T += "[]";

					Append(T);
					break;
				}

				case Expr_VariableDef VariableDef: {
					Compile(VariableDef.Type);
					Append(" {0}", VariableDef.Name);

					if (!OmmitSemicolon)
						AppendLine(";");

					break;
				}

				default: {
					throw new NotImplementedException("Could not compile expression of type " + Ex.GetType());
				}
			}
		}
	}
}
