using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("JJPortaudio")]
// #1145: the stream-ending and closed-stream tests reach the engine's internal
// Audio class without opening any device (Radios.Tests, RemoteAudioStreamEndTests).
[assembly: InternalsVisibleTo("Radios.Tests")]
// Sprint 49 Track Q3 repair 1: the audition's start-tone test drives the
// recorder's own file start (CaptureFileStart) with the real rendered tone,
// which only JJFlexWpf can render. No device is opened.
[assembly: InternalsVisibleTo("JJFlexWpf.Tests")]
[assembly: AssemblyDescription("Portaudio routines for JJRadio")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("JJShaffer.com")]
[assembly: AssemblyProduct("JJRadio")]
[assembly: AssemblyCopyright("Copyright © J.J. Shaffer 2017, 2018, 2019, 2020")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("ac0d237a-8224-4016-8001-3cf129cad335")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyVersion("1.2.2.1")]
[assembly: AssemblyFileVersion("1.2.2.1")]
