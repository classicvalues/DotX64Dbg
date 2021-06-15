﻿using System;

namespace Dotx64Dbg
{
    public struct ExceptionEventInfo
    {
        public uint ProcessId;
        public uint ThreadId;
        public bool FirstChance;
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public ulong ExceptionAddress;
    }

    public struct ThreadCreateEventInfo
    {
        public uint ProcessId;
        public uint ThreadId;
        public ulong Handle;
        public ulong ThreadLocalBase;
        public ulong StartAddress;
    }
    public struct ThreadExitEventInfo
    {
        public uint ProcessId;
        public uint ThreadId;
        public uint ExitCode;
    }

    public struct ProcessCreateEventInfo
    {
        public uint ProcessId;
        public uint ThreadId;
        public ulong FileHandle;
        public ulong ProcessHandle;
        public ulong ThreadHandle;
        public ulong ImageBase;
        public uint DebugInfoFileOffset;
        public uint DebugInfoSize;
        public ulong ThreadLocalBase;
        public ulong StartAddress;
        public ulong ImageName;
        public short Unicode;
    }

    public struct ProcessExitEventInfo
    {
        public uint ProcessId;
        public uint ThreadId;
        public uint ExitCode;
    }

    public static partial class Manager
    {

        public static void OnExceptionEvent(ExceptionEventInfo ev)
        {
            try
            {
                PluginManager.GetPluginInstances().ForEach(delegate (IPlugin instance)
                {
                    instance.OnExceptionEvent(ev);
                });
            }
            catch (System.Exception ex)
            {
                Console.WriteLine($"Exception {ex.ToString()}");
            }
        }

        public static void OnThreadCreateEvent(ThreadCreateEventInfo ev)
        {
            try
            {

                PluginManager.GetPluginInstances().ForEach(delegate (IPlugin instance)
                {
                    instance.OnThreadCreateEvent(ev);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception {ex.ToString()}");
            }
        }

        public static void OnProcessCreateEvent(ProcessCreateEventInfo ev)
        {
            try
            {
                PluginManager.GetPluginInstances().ForEach(delegate (IPlugin instance)
                {
                    instance.OnProcessCreateEvent(ev);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception {ex.ToString()}");
            }
        }
        public static void OnThreadExitEvent(ThreadExitEventInfo ev)
        {
            try
            {
                PluginManager.GetPluginInstances().ForEach(delegate (IPlugin instance)
                {
                    instance.OnThreadExitEvent(ev);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception {ex.ToString()}");
            }
        }

        public static void OnProcessExitEvent(ProcessExitEventInfo ev)
        {
            try
            {
                PluginManager.GetPluginInstances().ForEach(delegate (IPlugin instance)
                {
                    instance.OnProcessExitEvent(ev);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception {ex.ToString()}");
            }

        }
    }
}
