using System.Collections.Generic;

namespace HeroGame.Core.Crime
{
    /// <summary>
    /// What the custody screen shows a detained player: why they are held, what happens next and when, and what
    /// they can do about it (bail, an attorney, a plea). Plain data: built by the justice service offline and sent
    /// inside the player view online, so both screens show the same thing.
    /// </summary>
    public sealed class CustodyView
    {
        public bool InCustody;
        /// <summary>Serving a sentence (or a fines warrant) rather than awaiting a hearing.</summary>
        public bool ServingSentence;
        public long Today;
        /// <summary>Game second at which the next thing happens (the hearing, or release); -1 when nothing is scheduled.</summary>
        public long NextEventSecond = -1;
        public long HearingDay = -1;
        public long ReleaseDay = -1;
        public List<string> Charges = new List<string>();
        public string CaseSummary = "";
        /// <summary>Bail amount; 0 when there is none to post, -1 when bail was denied.</summary>
        public long BailCents;
        public bool CanPostBail;
        public string Counsel = "";
        public bool CanHireAttorney;
        public long AttorneyFeeCents;
        public bool PleaOffered;
        public bool PleadedGuilty;
        public float EvidenceStrength;

        /// <summary>Headline for the screen: what the player is waiting for.</summary>
        public string Headline =>
            !InCustody ? "Released" : ServingSentence ? "Serving your sentence" : HearingDay >= 0 ? "Held awaiting your hearing" : "In custody";

        /// <summary>"Release at the end of day 12" / "Hearing at the end of day 9" in plain words.</summary>
        public string NextStep =>
            !InCustody ? "You are free to go." :
            ServingSentence ? (ReleaseDay >= 0 ? "Release at the end of day " + ReleaseDay + "." : "Release date not set.") :
            HearingDay >= 0 ? "Your hearing is at the end of day " + HearingDay + "." : "";
    }
}
