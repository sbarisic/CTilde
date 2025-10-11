using CTilde.Expr;
using CTilde.FishAsm;
using System;
using System.CodeDom;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Langs
{
	public class FishAsmProvider : LangProvider
	{
		//bool OmmitSemicolon = false;
		FishCompileState State;

		public FishAsmProvider(FishCompileState State)
		{
			this.State = State;
		}

		string FormatArgs(object[] args)
		{
			string[] argsStr = new string[args.Length];

			for (int i = 0; i < args.Length; i++)
			{
				if (args[i] is Reg R)
				{
					argsStr[i] = FishUtils.RegToString(R);
				}
				else if (args[i] is uint UI)
				{
					argsStr[i] = string.Format("${0}", UI);
				}
				else
				{
					argsStr[i] = args[i].ToString();
				}
			}

			return string.Join(", ", argsStr);
		}

		void EmitInstruction(FishInst inst, params object[] args)
		{
			//Indent();
			AppendLine("{0} {1}", inst, FormatArgs(args));
			//Unindent();
		}

		void EmitRaw(string raw)
		{
			AppendLine(raw);
		}

		void EmitRaw(string fmt, params object[] args)
		{
			EmitRaw(string.Format(fmt, args));
		}

		public override void Compile(Expression Ex)
		{
			if (Ex == null)
				throw new ArgumentNullException(nameof(Ex));

			switch (Ex)
			{
				case Expr_Block Block:
					{
						//AppendLine("{");

						foreach (var E in Block.Expressions)
						{
							Compile(E);
						}

						//AppendLine("}");
						break;
					}

				case Expr_ClassDef ClassDef:
					{
						AppendLine("typedef struct {");

						foreach (var E in ClassDef.Variables)
						{
							Compile(E);
						}

						AppendLine("}} {0};", ClassDef.Name);

						foreach (var F in ClassDef.Functions)
						{
							Compile(F);
						}
						break;
					}

				case Expr_FuncDef FuncDef:
					{
						EmitRaw(".globl {0}", FuncDef.FuncName);
						State.DefineLabel(FuncDef.FuncName);

						if (FuncDef.FuncBody != null)
						{
							State.ClearVarOffsets();

							EmitRaw("{0}:", FuncDef.FuncName);
							Indent();
							State.IsInsideFunctionDef = true;
							EmitInstruction(FishInst.PUSH_REG, Reg.EBP);
							EmitInstruction(FishInst.MOVE_REG_REG, Reg.ESP, Reg.EBP);

							//OmmitSemicolon = true;
							//Compile(FuncDef.FuncReturnTypeDef);
							//Append(" {0}(", FuncDef.FuncName);

							Compile(FuncDef.FuncParams);

							//Append(")");
							//OmmitSemicolon = false;

							State.IsInsideFunctionDef = false;
							State.IsInsideFunctionBody = true;

							Compile(FuncDef.FuncBody);

							EmitInstruction(FishInst.LEAVE);
							EmitInstruction(FishInst.RET);
							State.IsInsideFunctionBody = false;
							Unindent();

							FishLabel[] Lbls = State.GetNewLabels();

							foreach (FishLabel L in Lbls)
							{
								EmitRaw(L.Name + ":");
								Indent();
								EmitRaw(".Raw {0}", L.Value);
								Unindent();
							}

						}
						break;
					}

				case Expr_Module Module:
					{
						foreach (var E in Module.Expressions)
							Compile(E);

						break;
					}

				case Expr_ParamsDef ParamsDef:
					{
						for (int i = 0; i < ParamsDef.Definitions.Count; i++)
						{
							ParamDefData ParamDef = ParamsDef.Definitions[i];
							//Compile(ParamDef);

							/*Compile(ParamDef.ParamType);
							Append(" {0}", ParamDef.Name);

							if (i + 1 < ParamsDef.Definitions.Count)
								Append(", ");*/

							int Size = State.GetTypeSize(ParamDef.ParamType);
							State.DefineVar(ParamDef.Name, Size, true);
						}

						break;
					}

				case Expr_TypeDef TypeDef:
					{
						string T = TypeDef.Type;

						if (TypeDef.IsPointer)
							T += "*";
						else if (TypeDef.IsArray)
							T += "[]";

						Append(T);
						break;
					}

				case Expr_VariableDef VariableDef:
					{
						/*Compile(VariableDef.Type);
						Append(" ");
						Compile(VariableDef.Ident);
						AppendLine(";");*/

						int Size = State.GetTypeSize(VariableDef.Type);
						State.DefineVar(VariableDef.Ident.Identifier, Size, false);
						EmitInstruction(FishInst.SUB_LONG_REG, (uint)Size, Reg.ESP);

						/*if (!OmmitSemicolon)
							AppendLine(";");*/

						break;
					}

				case Expr_AssignedVariableDef AssVariableDef:
					{
						Compile(AssVariableDef.VariableDef.Type);
						Append(" ");
						Compile(AssVariableDef.VariableDef.Ident);
						Append(" = ");

						Compile(AssVariableDef.AssignmentValue);

						AppendLine(";");
						break;
					}

				case Expr_AssignVariable AssVariable:
					{
						Compile(AssVariable.AssignmentValue);

						int VarID = State.GetVarOffset(AssVariable.Variable.Identifier);
						EmitInstruction(FishInst.MOVE_REG_OFFSET_REG, Reg.EAX, VarID, Reg.EBP);

						break;
					}

				case Expr_Identifier IdentifierEx:
					{
						EmitInstruction(FishInst.MOVE_OFFSET_REG_REG, State.GetVarOffset(IdentifierEx.Identifier), Reg.EBP, Reg.EAX);
						//Append(IdentifierEx.Identifier);
						break;
					}

				case Expr_ConstNumber NumberEx:
					{
						//Append(NumberEx.NumberLiteral);
						uint Num = uint.Parse(NumberEx.NumberLiteral);
						EmitInstruction(FishInst.MOVE_LONG_REG, Num, Reg.EAX);
						break;
					}

				case Expr_ConstString StringEx:
					{
						string LblName = State.DefineLabel(null, StringEx.StringLiteral);

						EmitInstruction(FishInst.MOVE_LONG_REG, LblName, Reg.EAX);
						//Append(StringEx.StringLiteral);
						break;
					}

				case Expr_MathOp MathExp:
					{
						Compile(MathExp.LExpr);

						EmitInstruction(FishInst.MOVE_REG_REG, Reg.EAX, Reg.EBX);

						//Append(" {0} ", MathExp.OpString);
						Compile(MathExp.RExpr);

						switch (MathExp.Op)
						{
							case MathOperation.Add:
								EmitInstruction(FishInst.ADD_REG_REG, Reg.EBX, Reg.EAX);
								break;

							default:
								throw new NotImplementedException();
						}

						break;
					}

				case Expr_FuncCall FuncCallExp:
					{
						if (FuncCallExp.Function.Identifier == "__asm")
						{
							foreach (var Arg in FuncCallExp.Arguments)
							{
								if (Arg is Expr_ConstString S)
								{
									EmitRaw(S.RawString);
								}
								else
								{
									throw new NotImplementedException("Only string literals are supported in __asm");
								}
							}
						}
						else
						{
							for (int i = 0; i < FuncCallExp.Arguments.Count; i++)
							{
								Compile(FuncCallExp.Arguments[i]);
								EmitInstruction(FishInst.PUSH_REG, Reg.EAX);
							}

							EmitInstruction(FishInst.MOVE_LONG_REG, FuncCallExp.Function.Identifier, Reg.EAX);
							EmitInstruction(FishInst.CALL_REG, Reg.EAX);

							EmitInstruction(FishInst.ADD_LONG_REG, (uint)(FuncCallExp.Arguments.Count * 4), Reg.ESP);

							/*throw new NotImplementedException();

							Compile(FuncCallExp.Function);

							Append("(");


							for (int i = 0; i < FuncCallExp.Arguments.Count; i++)
							{
								Compile(FuncCallExp.Arguments[i]);

								if (i < FuncCallExp.Arguments.Count - 1)
									Append(", ");
							}

							AppendLine(");");*/
						}
						break;
					}

				default:
					{
						throw new NotImplementedException("Could not compile expression of type " + Ex.GetType());
					}
			}
		}
	}
}
