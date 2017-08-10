﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Trinity.Diagnostics;
using Trinity.Extension;
using Trinity.Network;
using Trinity.Utilities;

[assembly: GraphEngineExtension]

namespace Trinity.FFI
{
    public class RuntimeManager : IStartupTask
    {
        #region Fields
        private List<ILanguageRuntimeProvider> m_providers = new List<ILanguageRuntimeProvider>();
        private Dictionary<string, ProgramRunner> m_runners = new Dictionary<string, ProgramRunner>();
        private FFIModule m_module = null;

        private bool m_comm_instance_started = false;
        private bool m_global_initialized = false;
        #endregion

        public void Run()
        {
            Global.Initialized += _OnGlobalInitialized;
            Global.CommunicationInstanceStarted += _OnCommunicationInstanceStart;
            Log.WriteLine("Trinity.FFI loaded.");
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void _OnCommunicationInstanceStart()
        {
            m_module = Global.CommunicationInstance.GetCommunicationModule<FFIModule>();
            m_comm_instance_started = true;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void _OnGlobalInitialized()
        {
            Log.WriteLine("Scanning for foreign runtime providers.");
            m_providers = AssemblyUtility.GetAllClassInstances(t => t.GetConstructor(new Type[] { }).Invoke(new object[] { }) as ILanguageRuntimeProvider);
            foreach (var runtime_provider in m_providers)
            {
                ProgramRunner runner = new ProgramRunner(runtime_provider);
                foreach (var format in runtime_provider.SupportedSuffix)
                {
                    m_runners[format] = runner;
                }
            }
            m_global_initialized = true;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void _TryStartFFIPrograms()
        {
            if (!m_global_initialized || !m_comm_instance_started) return;

            Log.WriteLine("Scanning for FFI Programs.");

            string dir;
            string[] files;

            dir = FFIConfig.Instance.ProgramDirectory;
            dir = FileUtility.CompletePath(dir, create_nonexistent: true);
            files = Directory.GetFiles(dir);

            foreach (var file in files)
            {
                try
                {
                    var suffix = Path.GetExtension(file);
                    if(m_provider_formats.TryGetValue(suffix, out var runtime_provider))
                    {
                        Log.WriteLine("Loading program {0}.", Path.GetFileName(file));
                    }
                }
                catch { }
            }
        }
    }
}