using System.Runtime.Versioning;

// Core is Win32 through and through (window management, job objects, WASAPI).
// The plain net10.0 TFM is kept deliberately so no UI framework can sneak in;
// this attribute tells the platform analyzer the truth instead.
[assembly: SupportedOSPlatform("windows")]
