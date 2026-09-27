using System;
using System.Collections.Generic;

namespace HeroGame.Core.Population
{
    /// <summary>
    /// First/last name pools used by the generator. Loaded from names.json; the built-in
    /// fallback keeps tests and tools working without content files.
    /// </summary>
    [Serializable]
    public sealed class NameTables
    {
        public List<string> FemaleFirst = new List<string>();
        public List<string> MaleFirst = new List<string>();
        public List<string> Last = new List<string>();

        public static NameTables Fallback()
        {
            return new NameTables
            {
                FemaleFirst = new List<string> { "Ana", "Brianna", "Carmen", "Dana", "Elena", "Faith", "Grace", "Hannah", "Imani", "Jasmine", "Keisha", "Lucia", "Maya", "Nora", "Olivia", "Priya", "Rosa", "Sofia", "Tamara", "Yesenia" },
                MaleFirst = new List<string> { "Andre", "Ben", "Carlos", "Darius", "Eli", "Felix", "Gabriel", "Hector", "Isaac", "Jamal", "Kevin", "Luis", "Marcus", "Nathan", "Omar", "Pedro", "Ray", "Samuel", "Trevor", "Victor" },
                Last = new List<string> { "Alvarez", "Bennett", "Castillo", "Dawson", "Ellis", "Flores", "Garza", "Harper", "Ibarra", "Jenkins", "Kowalski", "Lambert", "Medina", "Nguyen", "Okafor", "Patel", "Reed", "Salazar", "Thibodeaux", "Vasquez", "Washington", "Young" },
            };
        }

        public bool IsUsable => FemaleFirst.Count > 0 && MaleFirst.Count > 0 && Last.Count > 0;

        /// <summary>Picks a first name that differs from the surname (avoids "Harper Harper").</summary>
        public string PickFirst(Foundation.DeterministicRandom rng, Sex sex, string lastName)
        {
            var pool = sex == Sex.Female ? FemaleFirst : MaleFirst;
            var name = rng.Pick(pool);
            for (var i = 0; i < 4 && name == lastName; i++) name = rng.Pick(pool);
            return name;
        }
    }
}
