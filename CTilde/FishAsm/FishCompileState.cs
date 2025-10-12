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

		public FishVarDef(string Name, int EBPOffset, int Size, Expr_TypeDef TypeStr)
		{
			this.Name = Name;
			this.EBPOffset = EBPOffset;
			this.Size = Size;
			this.TypeStr = TypeStr;
		}
	}

	public class FishLabel
	{
		public string Name;
		public string Value;
		public bool Generated = false;

		public FishLabel(string Name)
		{
			this.Name = Name;
			Value = "";
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

		public int StackSize;
		public int FreeLabel = 0;

		List<FishVarDef> VarOffsets = new List<FishVarDef>();
		int ParamCount;
		int ArgCount;

		List<FishLabel> Labels = new List<FishLabel>();

		public string DefineFreeLabel(string LabelName)
		{
			if (!string.IsNullOrEmpty(LabelName))
				LabelName = "." + LabelName + "_" + (FreeLabel++).ToString("X4");

			DefineLabel(LabelName);

			return LabelName;
		}

		public void DefineLabel(string LabelName)
		{
			if (Labels.Any(l => l.Name == LabelName))
				throw new Exception(string.Format("Label '{0}' is already defined", LabelName));

			Labels.Add(new FishLabel(LabelName));
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
			if (Type.Type == "int" || Type.Type == "uint" || Type.Type == "float" || Type.Type == "bool" || Type.Type == "string")
				return 4;

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
			VarOffsets.Clear();
			StackSize = 0;
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

		void SetKeyValue(string Key, int EBPOffset, int Size, Expr_TypeDef TypeStr)
		{
			for (int i = 0; i < VarOffsets.Count; i++)
			{
				if (VarOffsets[i].Name == Key)
				{
					VarOffsets[i].EBPOffset = EBPOffset;
					VarOffsets[i].Size = Size;
					VarOffsets[i].TypeStr = TypeStr;
					return;
				}
			}

			VarOffsets.Add(new FishVarDef(Key, EBPOffset, Size, TypeStr));
		}

		public void DefineVar(string VarName, int EBPOffset, int Size, Expr_TypeDef TypeStr)
		{
			if (ContainsKey(VarName))
				throw new Exception(string.Format("Variable '{0}' is already defined", VarName));

			SetKeyValue(VarName, EBPOffset, Size, TypeStr);
			StackSize += Size;
		}

		public void DefineVar(string VarName, int Size, bool IsParam, Expr_TypeDef TypeStr)
		{
			if (IsParam)
			{
				DefineVar(VarName, 8 + (ParamCount * 4), Size, TypeStr);
			}
			else
			{
				DefineVar(VarName, -4 - (ArgCount * 4), Size, TypeStr);
			}

			if (IsParam)
				ParamCount++;
			else
				ArgCount++;
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

			throw new Exception(string.Format("Could not find variable '{0}'", VarName));
		}
	}
}
