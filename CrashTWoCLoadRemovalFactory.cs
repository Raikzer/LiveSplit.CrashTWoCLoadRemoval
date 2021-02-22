using LiveSplit.Model;
using LiveSplit.PokemonRedBlue;
using LiveSplit.UI.Components;
using System;

[assembly: ComponentFactory(typeof(CrashTWoCLoadRemovalFactory))]

namespace LiveSplit.PokemonRedBlue
{
    public class CrashTWoCLoadRemovalFactory : IComponentFactory
    {
        public string ComponentName
        {
            get { return "Crash TWoC Load Removal"; }
        }

        public ComponentCategory Category
        {
            get { return ComponentCategory.Control; }
        }

        public string Description
        {
            get { return "Automatically detects and removes loads (GameTime) for Crash The Wrath of Cortex."; }
        }

        public IComponent Create(LiveSplitState state)
        {
            return new CrashTWoCLoadRemovalComponent(state);
        }

        public string UpdateName
        {
            get { return ComponentName; }
        }
		public string UpdateURL => "https://raw.githubusercontent.com/SHTDJ/LiveSplit.CrashNSTLoadRemoval/master/";
		public string XMLURL => UpdateURL + "update.LiveSplit.CrashTWoCLoadRemoval.xml";
		

        public Version Version
        {
            get { return Version.Parse("2.5"); }
        }
    }
}
