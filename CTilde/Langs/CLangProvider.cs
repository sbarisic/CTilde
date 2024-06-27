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
		//bool OmmitSemicolon = false;

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
					//OmmitSemicolon = true;
					Compile(FuncDef.FuncReturnTypeDef);
					Append(" {0}(", FuncDef.FuncName);

					Compile(FuncDef.FuncParams);

					Append(")");
					//OmmitSemicolon = false;

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
						ParamDefData ParamDef = ParamsDef.Definitions[i];
						//Compile(ParamDef);

						Compile(ParamDef.ParamType);
						Append(" {0}", ParamDef.Name);

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
					Append(" ");
					Compile(VariableDef.Ident);
					AppendLine(";");

					/*if (!OmmitSemicolon)
						AppendLine(";");*/

					break;
				}

				case Expr_AssignedVariableDef AssVariableDef: {
					Compile(AssVariableDef.VariableDef.Type);
					Append(" ");
					Compile(AssVariableDef.VariableDef.Ident);
					Append(" = ");

					Compile(AssVariableDef.AssignmentValue);

					AppendLine(";");
					break;
				}

				case Expr_Identifier IdentifierEx: {
					Append(IdentifierEx.Identifier);
					break;
				}

				case Expr_ConstNumber NumberEx: {
					Append(NumberEx.NumberLiteral);
					break;
				}

				case Expr_MathOp MathExp: {
					Compile(MathExp.LExpr);

					Append(" {0} ", MathExp.OpString);

					Compile(MathExp.RExpr);
					break;
				}

				case Expr_FuncCall FuncCallExp: {
					Compile(FuncCallExp.Function);

					Append("(");


					for (int i = 0; i < FuncCallExp.Arguments.Count; i++) {
						Compile(FuncCallExp.Arguments[i]);

						if (i < FuncCallExp.Arguments.Count - 1)
							Append(", ");
					}

					AppendLine(");");
					break;
				}

				default: {
					throw new NotImplementedException("Could not compile expression of type " + Ex.GetType());
				}
			}
		}
	}
}
