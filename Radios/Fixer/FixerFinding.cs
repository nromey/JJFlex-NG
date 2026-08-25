using System;

namespace Radios.Fixer
{
    /// <summary>Who can put a finding right. Every finding is exactly one of
    /// these, and the page and report say which — because the three demand
    /// three different things of the operator: press a button, do a thing, or
    /// stop looking here.</summary>
    public enum FixOwner
    {
        /// <summary>JJ Flexible can fix it. The page offers a button AT THE
        /// POINT OF DETECTION — never "go to Settings", because an operator
        /// sent elsewhere loses their place. Never fixed silently: offer, act
        /// on a press, record what changed.</summary>
        Us = 0,

        /// <summary>The operator can fix it and we cannot — a cable, a Windows
        /// mute, a privacy setting, an open antenna port. Say exactly what to
        /// do, one sentence, no jargon.</summary>
        Operator,

        /// <summary>Nobody in this room can fix it. Say so, and stop implying
        /// otherwise.</summary>
        NobodyHere,
    }

    /// <summary>One thing a stage detected, classified by who can fix it.</summary>
    public sealed class FixerFinding
    {
        /// <summary>Stable identifier within its stage, e.g. "mme-in-use".
        /// The page's fix buttons and the run's fix records refer to it.</summary>
        public string Id { get; }

        public FixOwner Owner { get; }

        /// <summary>True when the operator must hear about this before anything
        /// else happens — it lands in the page's assertive live region, and the
        /// host pairs it with an earcon.</summary>
        public bool Critical { get; }

        /// <summary>What is wrong, in a person's voice. Plain and direct — it
        /// is fine to say what we think is wrong; supporting our own user is
        /// the job.</summary>
        public string WhatIsWrong { get; }

        /// <summary>For <see cref="FixOwner.Us"/>: the button's label.
        /// For <see cref="FixOwner.Operator"/>: the one sentence of what to do.
        /// For <see cref="FixOwner.NobodyHere"/>: the honest statement that
        /// nothing here will fix it.</summary>
        public string WhatToDo { get; }

        /// <summary>The fix action this finding's button invokes. Only an
        /// <see cref="FixOwner.Us"/> finding has one.</summary>
        public string FixActionId { get; }

        public FixerFinding(string id, FixOwner owner, string whatIsWrong, string whatToDo,
                            string fixActionId = null, bool critical = false)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("a finding needs an id", nameof(id));
            if (string.IsNullOrWhiteSpace(whatIsWrong))
                throw new ArgumentException("a finding must say what is wrong", nameof(whatIsWrong));
            if (string.IsNullOrWhiteSpace(whatToDo))
                throw new ArgumentException("a finding must say what to do about it, even when "
                    + "the answer is that nothing here can", nameof(whatToDo));

            // The taxonomy is enforced here rather than trusted downstream: a
            // button with nothing behind it and an action with no button are
            // both ways of fixing silently or not at all.
            if (owner == FixOwner.Us && string.IsNullOrWhiteSpace(fixActionId))
                throw new ArgumentException("a finding we can fix must name its fix action",
                                            nameof(fixActionId));
            if (owner != FixOwner.Us && !string.IsNullOrWhiteSpace(fixActionId))
                throw new ArgumentException("only a finding we can fix may carry a fix action",
                                            nameof(fixActionId));

            Id = id;
            Owner = owner;
            Critical = critical;
            WhatIsWrong = whatIsWrong;
            WhatToDo = whatToDo;
            FixActionId = fixActionId ?? "";
        }
    }
}
