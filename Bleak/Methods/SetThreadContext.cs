using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Bleak.Etc;
using Bleak.Services;

namespace Bleak.Methods
{
    internal class SetThreadContext : IDisposable
    {
        private readonly int _firstThreadId;
        
        private readonly IntPtr _mainWindowHandle;
        
        private readonly Properties _properties;

        internal SetThreadContext(Process process, string dllPath)
        {
            // Get the id of the first thread of the process
            
            _firstThreadId = process.Threads[0].Id;
            
            // Get the handle to the main window of the process
            
            _mainWindowHandle = process.MainWindowHandle;
            
            _properties = new Properties(process, dllPath);
        }
        
        public void Dispose()
        {
            _properties?.Dispose();
        }
        
        internal bool Inject()
        {   
            // Get the address of the LoadLibraryW method from kernel32.dll

            var loadLibraryAddress = Tools.GetRemoteProcAddress(_properties, "kernel32.dll", "LoadLibraryW");

            if (loadLibraryAddress == IntPtr.Zero)
            {
                ExceptionHandler.ThrowWin32Exception("Failed to find the address of the LoadLibraryW method in kernel32.dll");
            }
            
            // Allocate memory for the dll path in the process
            
            var dllPathAddress = IntPtr.Zero;

            try
            {
                dllPathAddress = _properties.MemoryModule.AllocateMemory(_properties.ProcessId, _properties.DllPath.Length);
            }

            catch (Win32Exception)
            {
                ExceptionHandler.ThrowWin32Exception("Failed to allocate memory for the dll path in the process");
            }
            
            // Write the dll path into the process
            
            var dllPathBytes = Encoding.Unicode.GetBytes(_properties.DllPath + "\0");

            try
            {
                _properties.MemoryModule.WriteMemory(_properties.ProcessId, dllPathAddress, dllPathBytes);
            }

            catch (Win32Exception)
            {
                ExceptionHandler.ThrowWin32Exception("Failed to write the dll path into the memory of the process");   
            }
            
            // Allocate memory for the shellcode in the process

            var shellcodeSize = _properties.IsWow64 ? 22 : 87;

            var shellcodeAddress = IntPtr.Zero;

            try
            {
                shellcodeAddress = _properties.MemoryModule.AllocateMemory(_properties.ProcessId, shellcodeSize);
            }

            catch (Win32Exception)
            {
                ExceptionHandler.ThrowWin32Exception("Failed to allocate memory for the shellcode in the process");              
            }
            
            // Open a handle to the first thread of the process

            var threadHandle = Native.OpenThread(Native.ThreadAccess.AllAccess, false, _firstThreadId);

            if (threadHandle is null)
            {
                ExceptionHandler.ThrowWin32Exception("Failed to open a handle to a thread in the process");
            }
            
            // Suspend the thread

            if (Native.SuspendThread(threadHandle) == -1)
            {
                ExceptionHandler.ThrowWin32Exception("Failed to suspend a thread in the process");
            }
            
            // If the process is x86
            
            if (_properties.IsWow64)
            {
                var threadContext = new Native.Context {Flags = Native.ContextFlags.ContextControl};

                // Store the thread context structure in a buffer
                
                var threadContextBuffer = Tools.StructureToPointer(threadContext);
                
                // Get the context of the thread and save it in the buffer
                
                if (!Native.GetThreadContext(threadHandle, threadContextBuffer))
                {    
                    ExceptionHandler.ThrowWin32Exception("Failed to get the context of a thread in the process");
                }
                
                // Read the new thread context structure from the buffer
                
                threadContext = Tools.PointerToStructure<Native.Context>(threadContextBuffer);
                
                // Save the instruction pointer

                var instructionPointer = threadContext.Eip;
                
                // Write the shellcode into the memory of the process

                var shellcodeBytes = Shellcode.CallLoadLibraryx86(instructionPointer, dllPathAddress, loadLibraryAddress);

                try
                {
                    _properties.MemoryModule.WriteMemory(_properties.ProcessId, shellcodeAddress, shellcodeBytes);
                }

                catch (Win32Exception)
                {
                    ExceptionHandler.ThrowWin32Exception("Failed to write the shellcode into the memory of the process");   
                }
                
                // Change the instruction pointer to the shellcode address

                threadContext.Eip = shellcodeAddress;
                
                // Store the thread context structure in a buffer
                
                threadContextBuffer = Tools.StructureToPointer(threadContext);

                // Set the context of the thread with the new context

                if (!Native.SetThreadContext(threadHandle, threadContextBuffer))
                {
                    ExceptionHandler.ThrowWin32Exception("Failed to set the context of a thread in the process");
                }
            }

            // If the process is x64
            
            else
            {   
                var threadContext = new Native.Context64 {Flags = Native.ContextFlags.ContextControl};

                // Store the thread context structure in a buffer
                
                var threadContextBuffer = Tools.StructureToPointer(threadContext);
                
                // Get the context of the thread and save it in the buffer
                
                if (!Native.GetThreadContext(threadHandle, threadContextBuffer))
                {    
                    ExceptionHandler.ThrowWin32Exception("Failed to get the context of a thread in the process");
                }

                // Read the new thread context structure from the buffer
                
                threadContext = Tools.PointerToStructure<Native.Context64>(threadContextBuffer);
                
                // Save the instruction pointer

                var instructionPointer = threadContext.Rip;

                // Write the shellcode into the memory of the process

                var shellcodeBytes = Shellcode.CallLoadLibraryx64(instructionPointer, dllPathAddress, loadLibraryAddress);

                try
                {
                    _properties.MemoryModule.WriteMemory(_properties.ProcessId, shellcodeAddress, shellcodeBytes);
                }

                catch (Win32Exception)
                {
                    ExceptionHandler.ThrowWin32Exception("Failed to write the shellcode into the memory of the process");   
                }
                
                // Change the instruction pointer to the shellcode address

                threadContext.Rip = shellcodeAddress;

                // Store the thread context structure in a buffer
                
                threadContextBuffer = Tools.StructureToPointer(threadContext);
                
                // Set the context of the thread with the new context

                if (!Native.SetThreadContext(threadHandle, threadContextBuffer))
                {
                    ExceptionHandler.ThrowWin32Exception("Failed to set the context of a thread in the process");
                }
            }
            
            // Resume the suspended thread

            if (Native.ResumeThread(threadHandle) == -1)
            {
                ExceptionHandler.ThrowWin32Exception("Failed to resume a thread in the process");
            }   
            
            // Switch to the process to load the dll
            
            Native.SwitchToThisWindow(_mainWindowHandle, true);

            // Buffer the execution by 10 milliseconds to avoid freeing memory before it has been referenced
            
            Thread.Sleep(10);
            
            // Free the memory previously allocated for the dll path

            try
            {
                _properties.MemoryModule.FreeMemory(_properties.ProcessId, dllPathAddress);
            }

            catch (Win32Exception)
            {
                ExceptionHandler.ThrowWin32Exception("Failed to free the memory allocated for the dll path in the process");   
            }
            
            // Free the memory previously allocated for the shellcode

            try
            {
                _properties.MemoryModule.FreeMemory(_properties.ProcessId, shellcodeAddress);
            }

            catch (Win32Exception)
            {
                ExceptionHandler.ThrowWin32Exception("Failed to free the memory allocated for the shellcode in the process");   
            }
            
            threadHandle?.Close();
            
            return true;
        }
    }
}