using System;
using AssemblyManagerPlugin.Infrastructure;
using Rhino;
using Rhino.PlugIns;

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

        /// <summary>
        /// The plug-in uses one service graph for its full lifetime. In particular, this
        /// keeps the static Rhino event subscriptions associated with one event processor.
        /// </summary>
        public ServiceFactory Services => ServiceFactory.Instance;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            var result = base.OnLoad(ref errorMessage);
            if (result == LoadReturnCode.Success)
                Services.LinkEvents.Start();

            return result;
        }

        protected override void OnShutdown()
        {
            Services.LinkEvents.Stop();
            base.OnShutdown();
        }
    }
}
