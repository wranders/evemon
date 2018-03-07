﻿using System.Xml.Serialization;
using EVEMon.XmlGenerator.Interfaces;

namespace EVEMon.XmlGenerator.StaticData
{
    public sealed class InvCategories : IHasID
    {
        [XmlElement("categoryID")]
        public int ID { get; set; }

        [XmlElement("categoryName")]
        public string Name { get; set; }

        [XmlElement("iconID")]
        public int? IconID { get; set; }

        [XmlElement("published")]
        public bool Published { get; set; }
    }
}
