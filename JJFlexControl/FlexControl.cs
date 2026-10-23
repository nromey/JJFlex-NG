using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml.Serialization;
using JJTrace;

namespace JJFlexControl
{
    /// <summary>
    /// Handle the FlexControl Knob.
    /// </summary>
    /// <remarks>
    /// The knob key definitions are static, see ValidKeys.
    /// The caller must pass a List of Action_t with the possible actions
    /// to the constructor in the "actions" parameter.
    /// The first action must be the null action.
    /// The "dflts" parameter may contain a List of KeyAction_t of defaults.
    /// Also see ConfiguredActions and KnobActionsMap.
    /// </remarks>
    public class FlexControl : IDisposable
    {
        private Serial async = null;
        /// <summary>
        /// True if there's a knob open on a port.
        /// </summary>
        public bool IsOpen
        {
            get
            {
                return ((async != null) && async.IsOpen);
            }
        }

        internal class Key_t
        {
            public string Command { get; private set; }
            public string Description { get; private set; }
            public Key_t(string cmd, string desc)
            {
                Command = cmd;
                Description = desc;
            }
        }
        // list of keys.
        internal static List<Key_t> ValidKeys = new List<Key_t>
        {
            new Key_t("D", "Tuning knob down"),
            new Key_t("U", "Tuning knob up"),
            new Key_t("S", "Tuning knob, short press"),
            new Key_t("C", "Tuning knob, double press"),
            new Key_t("L", "Tuning knob, long press"),
            new Key_t("X1S", "Left button, short press"),
            new Key_t("X1C", "Left button, double press"),
            new Key_t("X1L", "Left button, long press"),
            new Key_t("X2S", "Middle button, short press"),
            new Key_t("X2C", "Middle button, double press"),
            new Key_t("X2L", "Middle button, long press"),
            new Key_t("X3S", "Right button, short press"),
            new Key_t("X3C", "Right button, double press"),
            new Key_t("X3L", "Right button, long press"),
        };
        // dictionary mapping keys to key_t.
        internal Dictionary<string, Key_t> ValidKeysMap;

        /// <summary>
        /// Action handler callback.
        /// </summary>
        /// <param name="parm">parameter passed by interrupt handler</param>
        public delegate void KeyActionDel(object parm);
        /// <summary>
        /// Provides the current value.
        /// </summary>
        /// <param name="action">the Action_t for this action</param>
        /// <returns>the string value</returns>
        public delegate string ActionValueDel(Action_t action);
        /// <summary>
        /// Map action to description and handler.
        /// </summary>
        public class Action_t
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public KeyActionDel Action;
            public ActionValueDel ActionValue;
            public bool AlwaysActive; // true if active even when knob is off
            public Action_t() { }
            public Action_t(string name,string description,KeyActionDel action,
                ActionValueDel valueRtn = null, bool always = false)
            {
                Name = name;
                Description = description;
                Action = action;
                ActionValue = valueRtn;
                AlwaysActive = always;
            }
        }
        // List of valid actions.  Must be provided by the caller.
        // The first action must be the null action.
        internal List<Action_t> ValidActions;

        internal Action_t FindAction(string name, List<Action_t> actions)
        {
            Action_t rv = null;
            foreach(Action_t a in actions)
            {
                if (name == a.Name)
                {
                    rv = a;
                    break;
                }
            }
            return rv;
        }

        /// <summary>
        /// Map key to action.
        /// </summary>
        public class KeyAction_t
        {
            /// <summary>
            /// knob command, Command from Key_t
            /// </summary>
            public string Command;
            /// <summary>
            /// Name from Action_t
            /// </summary>
            public string ActionName;
            /// <summary>
            /// A persistent value, may be unused.
            /// </summary>
            public string PersistentValue;
            public KeyAction_t() { }
            public KeyAction_t(KeyAction_t k)
            {
                Command = k.Command;
                ActionName = k.ActionName;
            }
            public KeyAction_t(string command,string name)
            {
                Command = command;
                ActionName = name;
            }
        }
        internal List<KeyAction_t> DefaultKeysAndActions;
        /// <summary>
        /// List of keys mapped to actions.
        /// </summary>
        public List<KeyAction_t> ConfiguredActions
        {
            get
            {
                return ((configData != null) && (configData.Info != null)) ? configData.Info.Actions : null;
            }
        }

        internal KeyAction_t FindKeyAction(string key, List<KeyAction_t> keyActions)
        {
            KeyAction_t rv = null;
            foreach (KeyAction_t k in keyActions)
            {
                if (k.Command == key)
                {
                    rv = k;
                    break;
                }
            }
            return rv;
        }

        private KeyAction_t FindConfiguredAction(string name)
        {
            KeyAction_t rv = null;
            if (ConfiguredActions == null) return rv;

            foreach(KeyAction_t ka in ConfiguredActions)
            {
                if (name == ka.ActionName)
                {
                    rv = ka;
                    break;
                }
            }
            return rv;
        }

        /// <summary>
        /// Map knob commands, aka keys, to knob actions.
        /// </summary>
        public Dictionary<string, Action_t> KnobActionsMap = null;

        // Configuration data handling.
        private Config configData;

        /// <summary>
        /// Setup the knob
        /// </summary>
        /// <param name="fileName">path to serialized .xml config file for the knob</param>
        /// <param name="actions">List of possible actions</param>
        /// <param name="dflts">List of default keys and actions</param>
        /// <param name="autoConfig">True to automatically configure if no config file</param>
        public FlexControl(string fileName, List<Action_t> actions,
           List<KeyAction_t> dflts, bool autoConfig)
        {
            Tracing.TraceLine("FlexControl:" + fileName, TraceLevel.Info);
            // Create valid keys map.
            ValidKeysMap = new Dictionary<string, Key_t>();
            foreach(Key_t k in ValidKeys)
            {
                ValidKeysMap.Add(k.Command, k);
            }

            configData = new Config(this, fileName);
            ValidActions = actions;
            DefaultKeysAndActions = dflts;
            async = new Serial(this);

            // Read config data.
            configData.Read();
            // empty Info if no config data.

            // Let user select the port if none defined.
            if (autoConfig & (string.IsNullOrEmpty(configData.Info.Port)))
            {
                SelectPort();
            }
            if (!string.IsNullOrEmpty(configData.Info.Port)) async.Open(configData.Info.Port);

            // Let user map functions if none defined.
            if ((configData.Info.Actions == null) || (configData.Info.Actions.Count == 0))
            {
                // Nothing in config file.
                Tracing.TraceLine("FlexControl:using DefaultKeysAndActions", TraceLevel.Info);
                configData.Info.Actions = DefaultKeysAndActions;
                if (autoConfig) KeyConfigure();
            }
            if (KnobActionsMap == null) buildKnobActionsMap();
        }

        /// <summary>
        /// Allow the user to select a com port.
        /// </summary>
        public void SelectPort()
        {
            Tracing.TraceLine("SelectPort", TraceLevel.Info);
            ComInfo theForm = new ComInfo();
            // select configured port initially.
            theForm.ThePort = configData.Info.Port;
            if (((Form)theForm).ShowDialog() == DialogResult.OK)
            {
                if (configData.Info.Port != theForm.ThePort)
                {
                    // Port has changed.
                    Tracing.TraceLine("SelectPort:new port:" + theForm.ThePort, TraceLevel.Info);
                    configData.Info.Port = theForm.ThePort;
                    configData.Write();
                }
            }
            ((Form)theForm).Dispose();

            async.Close(); // close if was open.
            if (!string.IsNullOrEmpty(configData.Info.Port)) async.Open(configData.Info.Port);
        }

        /// <summary>
        /// Configure keys and actions
        /// </summary>
        public void KeyConfigure()
        {
            Tracing.TraceLine("KeyConfigure", TraceLevel.Info);
            ShowKeysAndActions theForm = new ShowKeysAndActions(this, configData.Info.Actions);
            if (theForm.ShowDialog() == DialogResult.OK)
            {
                Tracing.TraceLine("KeyConfigure:OK", TraceLevel.Info);
                configData.Info.Actions = theForm.actions;
                configData.Write();
            }
            ((Form)theForm).Dispose();

            buildKnobActionsMap();
        }

        private void buildKnobActionsMap()
        {
            Tracing.TraceLine("buildKnobActionsMap:" + (ConfiguredActions != null).ToString(), TraceLevel.Info);
            KnobActionsMap = new Dictionary<string, FlexControl.Action_t>();
            if (ConfiguredActions != null)
            {
                foreach (FlexControl.KeyAction_t k in ConfiguredActions)
                {
                    // Get the associated Action_t
                    foreach (FlexControl.Action_t a in ValidActions)
                    {
                        if ((k.ActionName == a.Name) && !KnobActionsMap.Keys.Contains(k.Command))
                        {
                            KnobActionsMap.Add(k.Command, a);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Get saved config value.
        /// </summary>
        /// <param name="name">the function name</param>
        /// <param name="dflt">the default if not configured</param>
        public string GetSavedStringValue(string name, string dflt)
        {
            KeyAction_t action = FindConfiguredAction(name);
            return ((action == null) || string.IsNullOrEmpty(action.PersistentValue)) ?
                dflt : action.PersistentValue;
        }

        /// <summary>
        /// Save a persistent value.
        /// </summary>
        /// <param name="name">the function name</param>
        /// <param name="value">the string value</param>
        public void SaveStringValue(string name, string value)
        {
            KeyAction_t action = FindConfiguredAction(name);
            Tracing.TraceLine("SaveStringValue:" + name + ' ' + value + ' ' +
                (string)((action == null) ? "no" : "yes"), TraceLevel.Info);
            if (action == null) return;

            action.PersistentValue = value;
            configData.Write();
        }

        /// <summary>
        /// Get saved config value.
        /// </summary>
        /// <param name="name">the function name</param>
        /// <param name="dflt">the default if not configured</param>
        /// <returns></returns>
        public bool GetSavedBooleanValue(string name, bool dflt)
        {
            KeyAction_t action = FindConfiguredAction(name);
            return ((action == null) || string.IsNullOrEmpty(action.PersistentValue)) ?
                dflt : (action.PersistentValue == "true") ? true : false;
        }

        /// <summary>
        /// Save a persistent value.
        /// </summary>
        /// <param name="name">the function name</param>
        /// <param name="value">the boolean value</param>
        public void SaveBooleanValue(string name, bool value)
        {
            KeyAction_t action = FindConfiguredAction(name);
            Tracing.TraceLine("SaveBooleanValue:" + name + ' ' + value.ToString() + ' ' +
                (string)((action == null) ? "no" : "yes"), TraceLevel.Info);
            if (action == null) return;

            action.PersistentValue = (value) ? "true" : "false";
            configData.Write();
        }

        /// <summary>
        /// Get saved config value.
        /// </summary>
        /// <param name="name">the function name</param>
        /// <param name="dflt">the default if not configured</param>
        public int GetSavedIntegerValue(string name, int dflt)
        {
            KeyAction_t action = FindConfiguredAction(name);
            return ((action == null) || string.IsNullOrEmpty(action.PersistentValue)) ?
                dflt : Int32.Parse(action.PersistentValue);
        }

        /// <summary>
        /// Save a persistent value.
        /// </summary>
        /// <param name="name">the function name</param>
        /// <param name="value">the integer value</param>
        public void SaveIntegerValue(string name, int value)
        {
            KeyAction_t action = FindConfiguredAction(name);
            Tracing.TraceLine("SaveIntegerValue:" + name + ' ' + value.ToString() + ' ' +
                (string)((action == null) ? "no" : "yes"), TraceLevel.Info);
            if (action == null) return;

            action.PersistentValue = value.ToString();
            configData.Write();
        }

        public delegate void KnobOutputDel(string data);
        /// <summary>
        /// Output from the knob.
        /// </summary>
        public event KnobOutputDel KnobOutput;
        /// <summary>
        /// The last token the knob sent that is not one of the fourteen input codes, such as the
        /// <c>F0304</c> it sends at every connect, or null if none has arrived. Kept rather than
        /// thrown away because it is probably the device saying what it is (#520); what it means
        /// is #523's question, not this property's.
        /// </summary>
        public string LastDeviceReport { get; private set; }

        internal void OnKnobOutput(string data)
        {
            if (KnobInputFilter.IsDeviceReport(data)) LastDeviceReport = data;
            if (KnobOutput != null)
            {
                Tracing.TraceLine("OnKnobOutput:" + data, TraceLevel.Info);
                KnobOutput(data);
            }
        }

        // Implement Dispose().
        private bool disposed = false;
        private Component component = new Component();
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            Tracing.TraceLine("Dispose:" + disposing.ToString(), TraceLevel.Info);
            if (!disposed)
            {
                if (disposing)
                {
                    component.Dispose();
                }
                if (async != null)
                {
                    async.Close();
                    async = null;
                }
                disposed = true;
            }
        }

        ~FlexControl()
        {
            Dispose(false);
        }
    }
}
