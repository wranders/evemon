namespace EVEMon.XmlGenerator.Models
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.DataAnnotations;
    using System.ComponentModel.DataAnnotations.Schema;
    using System.Data.Entity.Spatial;

    public partial class certCerts
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public int certId { get; set; }

        public int? groupID { get; set; }

		[StringLength(255)]
        public string name { get; set; }

        [StringLength(500)]
        public string description { get; set; }
    }
}
