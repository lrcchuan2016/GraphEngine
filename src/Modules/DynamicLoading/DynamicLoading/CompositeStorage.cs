﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Trinity.Storage;
using Trinity.Utilities;


namespace CompositeStorageExtension
{
    // Initializing when loading from storage.
    #region CompositeStorage
    public static class CompositeStorage
    {
        public static List<IStorageSchema> StorageSchema;

        public static List<IGenericCellOperations> GenericCellOperations;

        public static List<int> IDIntervals;

        public static Dictionary<string, int> CellTypeIDs;
            

        public static List<VersionRecorder> VersionRecorders;

    }
    #endregion

    // Allow to configure
    #region Constants 
    public static class ConfigConstant
    {
        private static int avgMaxAsmNum = 100;
        private static int avgCellNum = 10;
        private static int avgFieldNum = 3;

        public static int AvgMaxAsmNum { get => avgMaxAsmNum; set => avgMaxAsmNum = value; }
        public static int AvgCellNum { get => avgCellNum; set => avgCellNum = value; }
        public static int AvgFieldNum { get => avgFieldNum; set => avgFieldNum = value; }
    }
    #endregion 

    // Need to configure 
    #region Cmd
    public static class Cmd
    {
        public static string TSLCodeGenExeLocation = "Trinity.TSL.CodeGen.exe";
        // TODO conditionals for supporting both msbuild (netfx) and dotnet (coreclr)
        public static string DotNetExeLocation = "dotnet.exe";
        public static bool TSLCodeGenCmd(string arguments)
        {
            try
            {
                CmdCall(TSLCodeGenExeLocation, arguments);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return false;
            }
            return true;
        }

        public static bool DotNetBuildCmd(string arguments)
        {
            try
            {
                CmdCall(DotNetExeLocation, arguments);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return false;
            }
            return true;
        }

        public static void CmdCall(string cmd, string arguments)
        {
            Console.WriteLine("command:  " + cmd + " " + arguments);
            Process proc = new Process();
            proc.StartInfo.FileName = cmd;
            proc.StartInfo.Arguments = arguments;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.Start();
            Console.WriteLine(proc.StandardOutput.ReadToEnd());
            proc.WaitForExit();
        }
    }
    #endregion 

    // Static methods
    #region IntervalLookup 
    public static class GetIntervalIndex
    {
        public static int ByCellTypeID(int cellTypeID)
        {
            int seg = CompositeStorage.IDIntervals.FindLastIndex(seg_head => seg_head < cellTypeID);
            if (seg == -1 || seg == CompositeStorage.IDIntervals.Count)
                throw new CellTypeNotMatchException("Cell type id out of the valid range.");
            return seg;
        }

        public static int ByCellTypeName(string cellTypeName)
        {
            if (!CompositeStorage.CellTypeIDs.Keys.Contains(cellTypeName))
                throw new CellTypeNotMatchException("Unrecognized cell type string.");
            int seg = ByCellTypeID(CompositeStorage.CellTypeIDs[cellTypeName]);
            return seg;
        }
    }
    #endregion

    // Core of DynamicLoading
    #region Controller
    public static class Controller
    {
        #region State

        private static int _currentCellTypeOffset = 0;
        public static bool Initialized = false;
        public static int CurrentCellTypeOffset { get => _currentCellTypeOffset; }

        public static VersionRecorder CurrentVersion;

        #endregion
        #region CodeGen-Build-Load
        public static void CreateCSProj()
        {
            var path = Path.Combine(CurrentVersion.TslBuildDir, $"{CurrentVersion.Namespace}.csproj");
            File.WriteAllText(path, CSProj.Template);

        }
        private static bool CodeGen()
        {
#if DEBUG
            Directory.GetFiles(CurrentVersion.TslSrcDir, "*.tsl").ToList().ForEach(Console.WriteLine);
#endif

            return Cmd.TSLCodeGenCmd(
                    string.Join(" ", Directory.GetFiles(CurrentVersion.TslSrcDir, "*.tsl"))
                    + $" -offset {CurrentVersion.CellTypeOffset} -n {CurrentVersion.Namespace} -o {CurrentVersion.TslBuildDir}");
        }

        private static bool Build() =>
            Cmd.DotNetBuildCmd($"build {CurrentVersion.TslBuildDir} -o {CurrentVersion.AsmLoadDir}");

        private static Assembly Load()
        {
#if DEBUG
            Console.WriteLine("Loading " + Path.Combine(CurrentVersion.AsmLoadDir, $"{CurrentVersion.Namespace}.dll"));
#endif 
            return Assembly.LoadFrom(Path.Combine(CurrentVersion.AsmLoadDir, $"{CurrentVersion.Namespace}.dll"));
        }
        #endregion

        public static void Init(string IncludeDirectory)
        {
            CSProj.IncludeDirectory = IncludeDirectory;

            if (CompositeStorage.IDIntervals == null)
                CompositeStorage.IDIntervals = new List<int>(ConfigConstant.AvgMaxAsmNum * ConfigConstant.AvgCellNum){ _currentCellTypeOffset };
            if (CompositeStorage.StorageSchema == null)
                CompositeStorage.StorageSchema = new List<IStorageSchema>(ConfigConstant.AvgMaxAsmNum);
            if (CompositeStorage.CellTypeIDs == null)
                CompositeStorage.CellTypeIDs = new Dictionary<string, int>(ConfigConstant.AvgMaxAsmNum * ConfigConstant.AvgCellNum) { };
            if (CompositeStorage.StorageSchema == null)
                CompositeStorage.StorageSchema = new List<IStorageSchema>(ConfigConstant.AvgMaxAsmNum);
            if (CompositeStorage.GenericCellOperations == null)
                CompositeStorage.GenericCellOperations = new List<IGenericCellOperations>(ConfigConstant.AvgMaxAsmNum);

            Initialized = true;
        }

        public static void LoadFrom(
                    string tslSrcDir,
                    string tslBuildDir,
                    string moduleName,
                    string versionName = null)
        {
            if (!Initialized)
                throw new NotInitializedError();

            var asmLoadDir = Trinity.TrinityConfig.StorageRoot;

            asmLoadDir = FileUtility.CompletePath(Path.Combine(asmLoadDir, ""));
            CurrentVersion = new VersionRecorder(
                            _currentCellTypeOffset,
                            tslSrcDir,
                            tslBuildDir,
                            asmLoadDir,
                            moduleName,
                            versionName ?? DateTime.Now.ToString());
#if DEBUG
            Console.WriteLine("\n tslsrcDir: " + tslSrcDir +
                              "\n TslBuildDir : " + CurrentVersion.TslBuildDir +
                              "\n AsmLoadDir : " + CurrentVersion.AsmLoadDir);
#endif

            try
            {
                CreateCSProj();
            }
            catch (Exception e)
            {
                throw e;
            }
            if (!CodeGen())
            {
                throw new TSLCodeGenError();
            }

            if (!Build())
            {
                throw new TSLBuildError();
            }

            try
            {
                var asm = Load();

                var schema = AssemblyUtility.GetAllClassInstances<IStorageSchema>(assembly: asm).First();

                var cellOps = AssemblyUtility.GetAllClassInstances<IGenericCellOperations>(assembly: asm).First();

                var cellDescs = schema.CellDescriptors.ToList();

                CompositeStorage.StorageSchema.Add(schema);

                CompositeStorage.GenericCellOperations.Add(cellOps);

                int maxoffset = _currentCellTypeOffset;

                foreach (var cellDesc in cellDescs)
                {
                    CompositeStorage.CellTypeIDs[cellDesc.TypeName] = cellDesc.CellType;
                    // Assertion 1: New cell type does not crash into existing type space
                    Debug.Assert(_currentCellTypeOffset <= cellDesc.CellType);
                    maxoffset = Math.Max(maxoffset, cellDesc.CellType);

#if DEBUG
                    Console.WriteLine($"{cellDesc.TypeName}{{");

                    foreach(var fieldDesc in cellDesc.GetFieldDescriptors())
                    {    
                        Console.WriteLine($"    {fieldDesc.Name}: {fieldDesc.TypeName}");
                    }
                    Console.WriteLine("}");
#endif
                }
                CompositeStorage.IDIntervals.Add(_currentCellTypeOffset);
                // Assertion 2: intervals grow monotonically
                Debug.Assert(CompositeStorage.IDIntervals.OrderBy(_ => _).SequenceEqual(CompositeStorage.IDIntervals));
                _currentCellTypeOffset += cellDescs.Count + 1;
                // Assertion 3: The whole type id space is still compact
                Debug.Assert(_currentCellTypeOffset == maxoffset + 1);

                CompositeStorage.VersionRecorders.Add(CurrentVersion);
                CurrentVersion = null;
            }
            catch (Exception e)
            {
                throw new AsmLoadError(e.Message);
            }
        }
    }
    #endregion
}