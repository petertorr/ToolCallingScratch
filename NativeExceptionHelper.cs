using System.Diagnostics;
using System.Runtime.InteropServices;
using ToolCallingScratch.CrashDetection.NativeInterop;

namespace ToolCallingScratch.CrashDetection
{
    namespace NativeInterop
    {
        static class Win32
        {
            internal delegate long VectoredExceptionHandlerProc(ref EXCEPTION_POINTERS exceptionInfo);
            internal delegate long UnhandledExceptionFilterProc(ref EXCEPTION_POINTERS exceptionInfo);

            [DllImport("kernel32.dll")]
            internal static extern IntPtr SetUnhandledExceptionFilter(UnhandledExceptionFilterProc handler);

            [DllImport("kernel32.dll")]
            internal static extern IntPtr AddVectoredExceptionHandler(ulong first, VectoredExceptionHandlerProc handler);

            [DllImport("kernel32.dll")]
            internal static extern ulong RemoveVectoredExceptionHandler(IntPtr handler);
        }

        [StructLayout(LayoutKind.Sequential)]
        readonly struct EXCEPTION_POINTERS
        {
            readonly public IntPtr ExceptionRecord;
            readonly IntPtr ContextRecord;

            public bool IsValid => ExceptionRecord != IntPtr.Zero;
        }

        // This is the basic exception record. If we know it's a VC++
        // exception, we use the _VCPP variant.
        [StructLayout(LayoutKind.Sequential)]
        readonly struct EXCEPTION_RECORD64
        {
            readonly public int ExceptionCode;
            readonly public int ExceptionFlags;
            readonly IntPtr ExceptionRecord;
            readonly public IntPtr ExceptionAddress;
            readonly public int NumberParameters;
            readonly int __unusedAlignment;

            internal EXCEPTION_RECORD64? Next => ExceptionRecord != IntPtr.Zero ?
                Marshal.PtrToStructure<EXCEPTION_RECORD64>(ExceptionRecord) : null;
        }

        // VC++-specific variant, with the 4 extra fields
        [StructLayout(LayoutKind.Sequential)]
        readonly struct EXCEPTION_RECORD64_VCPP 
        {
            readonly public int ExceptionCode;
            readonly public int ExceptionFlags;
            readonly IntPtr ExceptionRecord;
            readonly public IntPtr ExceptionAddress;
            readonly public int NumberParameters;
            readonly int __unusedAlignment;
            readonly IntPtr ExceptionInformation0;
            readonly IntPtr VcppExceptionObject;
            readonly IntPtr Level1Ptr;
            readonly IntPtr HInst;

            // This crazy set of indirections is from https://devblogs.microsoft.com/oldnewthing/20100730-00/?p=13273/
            internal VcppThrownObjectInfo GetVcppExceptionInfo()
            {
                var l1 = Marshal.PtrToStructure<VcppLevel1Info>(Level1Ptr);
                var l2 = l1.Next(HInst);
                var l3 = l2.Next(HInst);
                var l4 = l3.Next(HInst);
                return l4;
            }

            public bool IsValid => Level1Ptr != IntPtr.Zero && NumberParameters == 4;

            // Visual C++ exception code.
            public const uint VcppExceptionCode = 0xE06D7363;
        }

        [StructLayout(LayoutKind.Sequential)]
        readonly struct VcppLevel1Info
        {
            readonly int _unused1;
            readonly int _unused2;
            readonly int _unused3;
            readonly int Level2Offset;
            internal VcppLevel2Info Next(IntPtr hinst)
            {
                return Marshal.PtrToStructure<VcppLevel2Info>(hinst + Level2Offset);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        readonly struct VcppLevel2Info
        {
            readonly int _unused1;
            readonly int Level3Offset;
            internal VcppLevel3Info Next(IntPtr hinst)
            {
                return Marshal.PtrToStructure<VcppLevel3Info>(hinst + Level3Offset);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        readonly struct VcppLevel3Info
        {
            readonly int _unused1;
            readonly int Level4Offset;
            internal VcppThrownObjectInfo Next(IntPtr hinst)
            {
                return Marshal.PtrToStructure<VcppThrownObjectInfo>(hinst + Level4Offset);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        readonly struct VcppThrownObjectInfo
        {
            readonly IntPtr _unused1;
            readonly IntPtr _unused2;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            readonly public string ClassName;
        }
    }

    /// <summary>
    /// Helper class that detects and prints the fatal runtime ONNX error.
    /// See: https://github.com/microsoft/foundry-local/issues/1191
    /// 
    /// In theory, this class is extensible to other kinds of exceptions,
    /// but right now it is VERY specific to this crash.
    /// </summary>
    static internal class NativeExceptionHelper
    {
        // The name of the object being thrown, as of writing...
        const string OnnxThrownExceptionClassName = ".?AVOnnxRuntimeException@onnxruntime@@";

        // Stash the current name so we can show it to the user.
        internal static string? CurrentModelName { get; set; }
        static string? lastModelName = null;

        // The unhandled crash appears during loading.
        internal static bool IsLoadingModel { get; set; } = false;

        static long VectoredHandler(ref EXCEPTION_POINTERS exceptionInfo)
        {
            return ExceptionHandler(ref exceptionInfo);
        }

        static long ExceptionHandler(ref EXCEPTION_POINTERS exceptionInfo)
        {
            if (!IsLoadingModel)
            {
                return 0;
            }

            if (!exceptionInfo.IsValid)
            {
                return 0;
            }

            var genericException = Marshal.PtrToStructure<EXCEPTION_RECORD64>(exceptionInfo.ExceptionRecord);
            if ((uint)(long)genericException.ExceptionCode != EXCEPTION_RECORD64_VCPP.VcppExceptionCode)
            {
                return 0;
            }

            // re-marshal as the VC++-specific one
            var vcppExceptionRecord = Marshal.PtrToStructure<EXCEPTION_RECORD64_VCPP>(exceptionInfo.ExceptionRecord);

            if (!vcppExceptionRecord.IsValid)
            {
                return 0;
            }
            var vcppExceptionName = vcppExceptionRecord.GetVcppExceptionInfo();
            var className = vcppExceptionName.ClassName;
            if (className != OnnxThrownExceptionClassName)
            {
                return 0;
            }

            // Check for the same error exception twice (probably due to 1st-chance and then final throw)?
            if (CurrentModelName == lastModelName)
            {
                return 0;
            }

            lastModelName = CurrentModelName;

            var text = $"`r*** `wVisual C++ Exception '{className}' detected `r***";
            Program.WriteLine($"{Environment.NewLine}{Environment.NewLine}`r{new string('*', text.Length - 6)}", true);
            Program.WriteLine(text, true);
            Program.WriteLine($"`r{new string('*', text.Length - 6)}", true);
            DumpExceptionRecordChain(genericException);
            Program.WriteLine($"{Environment.NewLine}`wIf the process crashes, add `c-i {CurrentModelName}`x to the command-line to ignore this model.", true);
            Program.WriteLine("`ySee https://github.com/microsoft/foundry-local/issues/1191 for bug on this issue.");

            return 0; // continue search
        }

        static void DumpExceptionRecordChain(EXCEPTION_RECORD64 exception)
        {
            string indent = "  ";
            do
            {
                Program.WriteLine($"{indent}Exception Address={exception.ExceptionAddress:X}, Code={exception.ExceptionCode:X}, Flags={exception.ExceptionFlags:X}, Params={exception.NumberParameters}.", true);

                if (!exception.Next.HasValue)
                {
                    break;
                }

                exception = exception.Next.Value;
                indent += "  ";
            } while (true);
        }

        static IntPtr vectorRegistration = IntPtr.Zero;
        internal static void SetupExceptionHandling()
        {
            // Nothing good happens from trying to mess with exceptions when VS is attached...
            if (Debugger.IsAttached)
            {
                return;
            }

            vectorRegistration = Win32.AddVectoredExceptionHandler(1, VectoredHandler);
        }
    }
}