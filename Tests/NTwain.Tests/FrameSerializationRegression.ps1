param([string]$AssemblyPath = "$PSScriptRoot\..\..\NTwain\bin\Debug\net462\NTwain.dll")
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath).Path)
Add-Type -ReferencedAssemblies (Resolve-Path $AssemblyPath).Path -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using NTwain.Data;
public static class FrameSerializationRegression
{
    public static void Run()
    {
        IntPtr buffer = Marshal.AllocHGlobal(16);
        try
        {
            // Include both sides of the fractional sign bit and negative coordinates.
            foreach (float value in new float[] { 0f, 0.25f, 0.5f, 0.75f, 1.716141f, 9.983856f, 11.69292f, -0.75f })
            {
                TWFix32 expected = value;
                int offset = 0;
                buffer.WriteValue(ref offset, ItemType.Fix32, expected);
                if ((ushort)Marshal.ReadInt16(buffer, 2) != expected.Fraction)
                    throw new Exception("Fraction bits corrupted for " + value);
                offset = 0;
                TWFix32 actual = (TWFix32)buffer.ReadValue(ref offset, ItemType.Fix32);
                if (!expected.Equals(actual)) throw new Exception("FIX32 round trip failed for " + value);
            }
            TWFrame frame = new TWFrame { Left = 1.716141f, Top = 0, Right = 9.983856f, Bottom = 11.69292f };
            int frameOffset = 0;
            buffer.WriteValue(ref frameOffset, ItemType.Frame, frame);
            frameOffset = 0;
            TWFrame result = (TWFrame)buffer.ReadValue(ref frameOffset, ItemType.Frame);
            if (frame.Left != result.Left || frame.Top != result.Top || frame.Right != result.Right || frame.Bottom != result.Bottom)
                throw new Exception("A4 scan frame corrupted: " + result);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
"@
[FrameSerializationRegression]::Run()
Write-Output 'PASS: FIX32 fractional bits and A4 frame round trip'