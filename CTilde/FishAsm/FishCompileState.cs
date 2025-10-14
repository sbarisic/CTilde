using CTilde.Expr;
using System;
using System.Collections.Generic;
using System.Diagnostics.SymbolStore;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.FishAsm
{
	public class FishVarDef
	{
		public string Name;
		public int EBPOffset;
		public int Size;
		public Expr_TypeDef TypeStr;
		public bool Global;
		public bool Param;

		public FishVarDef(string Name, int EBPOffset, int Size, Expr_TypeDef TypeStr, bool Global, bool Param)
		{
			this.Name = Name;
			this.EBPOffset = EBPOffset;
			this.Size = Size;
			this.TypeStr = TypeStr;
			this.Global = Global;
			this.Param = Param;
		}

		public override string ToString()
		{
			return string.Format("FishVarDef({0}, EBPOffset {1}, Size {2}, {3}, Global {4})", Name, EBPOffset, Size, TypeStr?.ToString() ?? "null", Global);
		}
	}

	public class FishLabel
	{
		public string Name;
		public string Value;
		public bool Generated = false;
		public bool Global = false;

		public FishLabel(string Name, bool Global)
		{
			this.Name = Name;
			Value = "";
			this.Global = Global;
		}

		public FishLabel(string Name, string Value)
		{
			this.Name = Name;
			this.Value = Value;
		}
	}

	public class FishCompileState
	{
		public bool IsInsideFunctionBody = false;
		public bool IsInsideFunctionDef = false;
		public bool IndexEmitOnlyAddress = false;

		public int StackSize;
		public int FreeLabel = 0;

		List<FishVarDef> VarOffsets = new List<FishVarDef>();
		int ParamOffset;
		int ArgOffset;

		List<FishLabel> Labels = new List<FishLabel>();

		public string DefineFreeLabel(string LabelName, bool Global)
		{
			if (!string.IsNullOrEmpty(LabelName))
				LabelName = "." + LabelName + "_" + (FreeLabel++).ToString("X4");

			DefineLabel(LabelName, Global);
			return LabelName;
		}

		public void DefineLabel(string LabelName, bool Global)
		{
			if (Labels.Any(l => l.Name == LabelName))
				throw new Exception(string.Format("Label '{0}' is already defined", LabelName));

			FreeLabel++;
			Labels.Add(new FishLabel(LabelName, Global));
		}

		public FishLabel[] GetNewLabels()
		{
			List<FishLabel> Lbls = new List<FishLabel>();

			foreach (FishLabel label in Labels)
			{
				if (!label.Name.StartsWith(".L_"))
					continue;

				if (!label.Generated)
					Lbls.Add(label);
			}

			return Lbls.ToArray();
		}

		public string DefineLabel(string LabelName, string Value)
		{
			if (string.IsNullOrEmpty(LabelName))
				LabelName = ".L_" + (FreeLabel++).ToString("X4");

			if (Labels.Any(l => l.Name == LabelName))
				throw new Exception(string.Format("Label '{0}' is already defined", LabelName));

			Labels.Add(new FishLabel(LabelName, Value));
			return LabelName;
		}

		public FishLabel GetLabel(string LabelName)
		{
			FishLabel Label = Labels.FirstOrDefault(l => l.Name == LabelName);

			if (Label == null)
				throw new Exception(string.Format("Could not find label '{0}'", LabelName));

			return Label;
		}

		int GetRawTypeSize(Expr_TypeDef Type)
		{
			if (Type.Type == "int" || Type.Type == "uint" || Type.Type == "float" || Type.Type == "string")
				return 4;
			else if (Type.Type == "byte" || Type.Type == "bool")
				return 1;

			throw new NotImplementedException();
		}

		public int GetTypeSize(Expr_TypeDef Type)
		{
			if (Type.IsPointer || Type.IsArray)
				return 4;

			return GetRawTypeSize(Type);
		}

		public int GetPointerTypeSize(Expr_TypeDef Type)
		{
			if (Type.Type == "string")
				return 1; // string is array of bytes

			if (!(Type.IsPointer || Type.IsArray))
				throw new Exception("Not pointer or array type");

			return GetRawTypeSize(Type);
		}

		public void ClearVarOffsets()
		{
			List<FishVarDef> RemoveList = new List<FishVarDef>();

			foreach (var VO in VarOffsets)
			{
				if (Labels.Where(L => L.Name == VO.Name && L.Global).Count() <= 0)
				{
					RemoveList.Add(VO);
					continue;
				}

				if (VO.Param)
					RemoveList.Add(VO);
			}

			foreach (var RemoveItm in RemoveList)
			{
				VarOffsets.Remove(RemoveItm);
			}

			//VarOffsets.Clear();
			StackSize = 0;
			ParamOffset = 0;
		}

		public void ClearArgOffset()
		{
			ArgOffset = 0;
		}

		bool ContainsKey(string Key)
		{
			for (int i = 0; i < VarOffsets.Count; i++)
			{
				if (VarOffsets[i].Name == Key)
					return true;
			}

			return false;
		}

		FishVarDef GetKeyValue(string Key)
		{
			for (int i = 0; i < VarOffsets.Count; i++)
			{
				if (VarOffsets[i].Name == Key)
					return VarOffsets[i];
			}

			return null;
		}

		void SetKeyValue(string Key, int EBPOffset, int Size, Expr_TypeDef TypeStr, bool Global, bool Param)
		{
			if (Global)
			{
				EBPOffset = 0;
			}

			for (int i = 0; i < VarOffsets.Count; i++)
			{
				if (VarOffsets[i].Name == Key)
				{
					VarOffsets[i].EBPOffset = EBPOffset;
					VarOffsets[i].Size = Size;
					VarOffsets[i].TypeStr = TypeStr;
					VarOffsets[i].Global = Global;
					VarOffsets[i].Param = Param;
					return;
				}
			}

			VarOffsets.Add(new FishVarDef(Key, EBPOffset, Size, TypeStr, Global, Param));
		}

		public void DefineVar(string VarName, int EBPOffset, int Size, Expr_TypeDef TypeStr, bool Global, bool Param)
		{
			if (ContainsKey(VarName))
				throw new Exception(string.Format("Variable '{0}' is already defined", VarName));

			SetKeyValue(VarName, EBPOffset, Size, TypeStr, Global, Param);
			StackSize += Size;
		}

		public void DefineVar(string VarName, int Size, bool IsParam, Expr_TypeDef TypeStr, bool Global, bool Param)
		{
			if (IsParam)
			{
				DefineVar(VarName, 4 + (ParamOffset + Size), Size, TypeStr, Global, Param);
			}
			else
			{
				/*int Offset = 0;

				for (int i = 0; i < VarOffsets.Count; i++)
				{
					if (VarOffsets[i].Global)
						continue;

					Offset += VarOffsets[i].Size;
				}*/

				DefineVar(VarName, -4 - (ArgOffset), Size, TypeStr, Global, Param);
			}

			if (!Global)
			{
				if (IsParam)
					ParamOffset += Size;
				else
					ArgOffset += Size;
			}
		}

		/*public void GetVarS(string VarName)
		{
			if (ContainsKey(VarName))
				return GetKeyValue(VarName).EBPOffset;

			throw new Exception(string.Format("Could not find variable '{0}'", VarName));
		}*/

		public int GetVarOffset(string VarName)
		{
			if (ContainsKey(VarName))
				return GetKeyValue(VarName).EBPOffset;

			throw new Exception(string.Format("Could not find variable '{0}'", VarName));
		}

		public Expr_TypeDef GetVarType(string VarName)
		{
			if (ContainsKey(VarName))
				return GetKeyValue(VarName).TypeStr;

			//throw new Exception(string.Format("Could not find variable '{0}'", VarName));
			return null;
		}

		public bool IsVarGlobal(string VarName)
		{
			if (Labels.Where(L => L.Global && L.Name == VarName).Count() > 0)
				return true;

			return false;
		}

		Stack<string> BreakLabels = new Stack<string>();

		public void PushBreakLabel(string BreakLabel)
		{
			BreakLabels.Push(BreakLabel);
		}

		public string PeekBreakLabel()
		{
			if (BreakLabels.Count == 0)
				throw new Exception("No break label in stack");

			return BreakLabels.Peek();
		}

		public string PopBreakLabel()
		{
			return BreakLabels.Pop();
		}
	}
}
