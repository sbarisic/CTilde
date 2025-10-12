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

							if (!FuncDef.Naked)
							{
								EmitInstruction(FishInst.PUSH_REG, Reg.EBP);
								EmitInstruction(FishInst.MOVE_REG_REG, Reg.ESP, Reg.EBP);
							}

							//OmmitSemicolon = true;
							//Compile(FuncDef.FuncReturnTypeDef);
							//Append(" {0}(", FuncDef.FuncName);

							Compile(FuncDef.FuncParams);

							//Append(")");
							//OmmitSemicolon = false;

							State.IsInsideFunctionDef = false;
							State.IsInsideFunctionBody = true;

							Compile(FuncDef.FuncBody);

							if (!FuncDef.Naked)
							{
								EmitInstruction(FishInst.LEAVE);
								EmitInstruction(FishInst.RET);
							}
							State.IsInsideFunctionBody = false;
							Unindent();

							FishLabel[] Lbls = State.GetNewLabels();

							foreach (FishLabel L in Lbls)
							{
								EmitRaw(L.Name + ":");
								Indent();

								if (L.Value.StartsWith("\"") && L.Value.EndsWith("\""))
								{
									EmitRaw(".String {0}", L.Value);
								}
								else
								{
									EmitRaw(".long {0}", L.Value);
								}

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
							State.DefineVar(ParamDef.Name, Size, true, ParamDef.ParamType);
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
						State.DefineVar(VariableDef.Ident.Identifier, Size, false, VariableDef.Type);
						EmitInstruction(FishInst.SUB_LONG_REG, (uint)Size, Reg.ESP);

						/*if (!OmmitSemicolon)
							AppendLine(";");*/

						break;
					}

				case Expr_AssignedVariableDef AssVariableDef:
					{
						int Size = State.GetTypeSize(AssVariableDef.VariableDef.Type);
						State.DefineVar(AssVariableDef.VariableDef.Ident.Identifier, Size, false, AssVariableDef.VariableDef.Type);
						EmitInstruction(FishInst.SUB_LONG_REG, (uint)Size, Reg.ESP);

						Compile(AssVariableDef.AssignmentValue);

						int VarID = State.GetVarOffset(AssVariableDef.VariableDef.Ident.Identifier);
						EmitInstruction(FishInst.MOVE_REG_OFFSET_REG, Reg.EAX, VarID, Reg.EBP);
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
						Expr_TypeDef TypeDef = State.GetVarType(IdentifierEx.Identifier);
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

				case Expr_ConstChar CharEx:
					{
						EmitInstruction(FishInst.MOVES_LONG_REG, (uint)CharEx.CharLiteral, Reg.EAX);
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

				case Expr_ComparisonOp CompExpr:
					{
						Compile(CompExpr.LExpr);
						EmitInstruction(FishInst.MOVE_REG_REG, Reg.EAX, Reg.EBX);
						Compile(CompExpr.RExpr);

						EmitInstruction(FishInst.CMP_REG_REG, Reg.EAX, Reg.EBX);

						/*switch (CompExpr.Op)
						{
							case ComparisonOp.Equals:
								throw new NotImplementedException();
								break;

							case ComparisonOp.NotEquals:
								EmitRaw("# NOTEQUALS"); 
								throw new NotImplementedException();
								break;

							default:
								throw new NotImplementedException();
						}*/

						break;
					}

				case Expr_IfElseStatement IfExpr:
					{
						string EndLblName = State.DefineFreeLabel("ENDIF");
						string ElseLblName = EndLblName;

						if (IfExpr.ElseBody != null)
							ElseLblName = State.DefineFreeLabel("ELSE");

						Compile(IfExpr.ConditionValue);

						if (IfExpr.ConditionValue is Expr_ComparisonOp Cmp)
						{
							if (Cmp.Op == ComparisonOp.Equals)
							{
								EmitInstruction(FishInst.JUMP_IF_NOT_ZERO_LONG, ElseLblName);
							}
							else if (Cmp.Op == ComparisonOp.NotEquals)
							{
								EmitInstruction(FishInst.JUMP_IF_ZERO_LONG, ElseLblName);
							}
							else
								throw new NotImplementedException();

						}
						else
							throw new NotImplementedException();

						Compile(IfExpr.Body);

						if (IfExpr.ElseBody != null)
						{
							EmitInstruction(FishInst.JUMP_LONG, EndLblName);
							EmitRaw("{0}:", ElseLblName);
							Compile(IfExpr.ElseBody);
						}

						EmitRaw("{0}:", EndLblName);
						break;
					}

				case Expr_WhileStatement WhileExpr:
					{
						string LblName = State.DefineFreeLabel("WHILE");
						string EndLblName = State.DefineFreeLabel("ENDWHILE");
						EmitRaw("{0}:", LblName);
						Compile(WhileExpr.ConditionValue);

						if (WhileExpr.ConditionValue is Expr_ComparisonOp Cmp)
						{
							if (Cmp.Op == ComparisonOp.Equals)
							{
								EmitInstruction(FishInst.JUMP_IF_NOT_ZERO_LONG, EndLblName);
							}
							else if (Cmp.Op == ComparisonOp.NotEquals)
							{
								EmitInstruction(FishInst.JUMP_IF_ZERO_LONG, EndLblName);
							}
							else
								throw new NotImplementedException();

						}
						else
							throw new NotImplementedException();

						//EmitRaw("# Body goes here");
						Compile(WhileExpr.Body);

						EmitInstruction(FishInst.JUMP_LONG, LblName);
						EmitRaw("{0}:", EndLblName);
						//throw new NotImplementedException();
						break;
					}

				case Expr_IndexOp IndexExpr:
					{
						Compile(IndexExpr.IndexValExpr);
						EmitInstruction(FishInst.MOVE_REG_REG, Reg.EAX, Reg.EBX);

						Compile(IndexExpr.LExpr);
						EmitInstruction(FishInst.ADD_REG_REG, Reg.EBX, Reg.EAX);

						Expr_Identifier IDExpr = IndexExpr.LExpr as Expr_Identifier;
						Expr_TypeDef VarType = State.GetVarType(IDExpr.Identifier);

						int CopyBytes = State.GetPointerTypeSize(VarType);

						if (CopyBytes == 1)
							EmitInstruction(FishInst.MOVES_OFFSET_REG_REG, 0, Reg.EAX, Reg.EAX);
						else if (CopyBytes == 2)
							EmitInstruction(FishInst.MOVEZ_OFFSET_REG_REG, 0, Reg.EAX, Reg.EAX);
						else if (CopyBytes == 4)
							EmitInstruction(FishInst.MOVE_OFFSET_REG_REG, 0, Reg.EAX, Reg.EAX);
						else
							throw new NotImplementedException();

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
						else if (FuncCallExp.Function.Identifier == "syscall_2")
						{
							if (FuncCallExp.Arguments.Count != 2)
								throw new Exception("syscall_2 requires exactly 2 arguments");

							Expr_ConstNumber NumExp = FuncCallExp.Arguments[0] as Expr_ConstNumber;
							Expression A0 = FuncCallExp.Arguments[1];

							Compile(A0);
							EmitInstruction(FishInst.PUSH_REG, Reg.EAX);
							EmitInstruction(FishInst.MOVE_LONG_REG, "$" + NumExp.NumberLiteral, Reg.EAX);
							EmitInstruction(FishInst.PUSH_REG, Reg.EAX);

							EmitInstruction(FishInst.SYSCALL_2);

							/*foreach (var Arg in FuncCallExp.Arguments)
							{
								if (Arg is Expr_ConstString S)
								{
									EmitRaw(S.RawString);
								}
								else
								{
									throw new NotImplementedException("Only string literals are supported in __asm");
								}
							}*/
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
