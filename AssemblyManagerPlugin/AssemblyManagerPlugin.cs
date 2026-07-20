using System;
using AssemblyManagerPlugin.Infrastructure;
using Rhino;

namespace AssemblyManagerPlugin
{
    ///<summary>
    /// <para>Every RhinoCommon .rhp assembly must have one and only one PlugIn-derived
    /// class. DO NOT create instances of this class yourself. It is the
    /// responsibility of Rhino to create an instance of this class.</para>
    /// <para>To complete plug-in information, please also see all PlugInDescription
    /// attributes in AssemblyInfo.cs (you might need to click "Project" ->
    /// "Show All Files" to see it in the "Solution Explorer" window).</para>
    ///</summary>
    public class AssemblyManagerPlugin : Rhino.PlugIns.PlugIn
    {
        public AssemblyManagerPlugin()
        {
            Instance = this;
        }
        
        ///<summary>Gets the only instance of the AssemblyManagerPlugin plug-in.</summary>
        public static AssemblyManagerPlugin Instance { get; private set; } = null!;

        public ServiceFactory Services { get; } = new();

        // You can override methods here to change the plug-in behavior on
        // loading and shut down, add options pages to the Rhino _Option command
        // and maintain plug-in wide options in a document.
    }
}
