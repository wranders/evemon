﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using EVEMon.Common.Collections;
using EVEMon.Common.Constants;
using EVEMon.Common.Enumerations;
using EVEMon.Common.Serialization.Datafiles;
using EVEMon.XmlGenerator.Extensions;
using EVEMon.XmlGenerator.Interfaces;
using EVEMon.XmlGenerator.Providers;
using EVEMon.XmlGenerator.StaticData;
using EVEMon.XmlGenerator.Utils;

namespace EVEMon.XmlGenerator.Datafiles
{
	internal static class Items
	{
		private static List<InvMarketGroups> s_injectedMarketGroups;
		private static List<InvTypes> s_nullMarketItems;

		/// <summary>
		/// Generate the items datafile.
		/// </summary>
		internal static void GenerateDatafile()
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			Util.ResetCounters();

			Console.WriteLine();
			Console.Write(@"Generating items datafile... ");

			// Move non existing makret group to custom market group
			ConfigureNonExistingMarketGroupItems();

			// Create custom market groups that don't exist in EVE
			ConfigureNullMarketItems();

			Dictionary<int, SerializableMarketGroup> groups = new Dictionary<int, SerializableMarketGroup>();

			// Create the market groups
			CreateMarketGroups(groups);

			// Create the parent-children groups relations
			foreach (SerializableMarketGroup group in groups.Values)
			{
				IEnumerable<SerializableMarketGroup> children = Database.InvMarketGroupsTable.Concat(
					s_injectedMarketGroups).Where(x => x.ParentID.GetValueOrDefault() == group.ID).Select(
						x => groups[x.ID]).OrderBy(x => x.Name);

				group.SubGroups.AddRange(children);
			}

			// Pick the family
			SetItemFamilyByMarketGroup(groups[DBConstants.BlueprintsMarketGroupID], ItemFamily.Blueprint);
			SetItemFamilyByMarketGroup(groups[DBConstants.ShipsMarketGroupID], ItemFamily.Ship);
			SetItemFamilyByMarketGroup(groups[DBConstants.ImplantsMarketGroupID], ItemFamily.Implant);
			SetItemFamilyByMarketGroup(groups[DBConstants.DronesMarketGroupID], ItemFamily.Drone);
			SetItemFamilyByMarketGroup(groups[DBConstants.StarbaseStructuresMarketGroupID], ItemFamily.StarbaseStructure);

			// Sort groups
			IEnumerable<SerializableMarketGroup> rootGroups = Database.InvMarketGroupsTable.Concat(s_injectedMarketGroups).Where(
				x => !x.ParentID.HasValue).Select(x => groups[x.ID]).OrderBy(x => x.Name);

			// Reset the custom market groups
			s_nullMarketItems.ForEach(srcItem => srcItem.MarketGroupID = null);

			// Serialize
			ItemsDatafile datafile = new ItemsDatafile();
			datafile.MarketGroups.AddRange(rootGroups);

			Util.DisplayEndTime(stopwatch);

			// DEBUG: Find which items have not been generated
			if (Debugger.IsAttached)
			{
				var itemids = groups.Values.SelectMany(x => x.Items).Select(y => y.ID).ToList();
				var diff = Database.InvTypesTable.Where(item => !itemids.Contains(item.ID)).ToList();

				if (diff.Any())
					Console.WriteLine("{0} items were not generated.", diff.Count);
			}

			Util.SerializeXml(datafile, DatafileConstants.ItemsDatafile);
		}

		/// <summary>
		/// Creates the market groups.
		/// </summary>
		/// <param name="groups">The groups.</param>
		private static void CreateMarketGroups(IDictionary<int, SerializableMarketGroup> groups)
		{
			foreach (InvMarketGroups marketGroup in Database.InvMarketGroupsTable.Concat(s_injectedMarketGroups))
			{
				SerializableMarketGroup group = new SerializableMarketGroup { ID = marketGroup.ID, Name = marketGroup.Name };
				groups[marketGroup.ID] = group;

				// Add the items in this group; excluding the implants we are adding below
				List<SerializableItem> items = new List<SerializableItem>();
				foreach (InvTypes srcItem in Database.InvTypesTable.Where(
					item => !item.Generated && item.MarketGroupID.GetValueOrDefault() == marketGroup.ID).Where(
						srcItem => marketGroup.ParentID != DBConstants.RootNonMarketGroupID ||
								   Database.InvGroupsTable[srcItem.GroupID].CategoryID != DBConstants.ImplantCategoryID ||
								   srcItem.GroupID == DBConstants.CyberLearningImplantsGroupID))
				{
					CreateItem(srcItem, items);
				}

				// Store the items
				group.Items.AddRange(items.OrderBy(x => x.Name));
			}
		}

		/// <summary>
		/// Configures the null market items.
		/// </summary>
		private static void ConfigureNullMarketItems()
		{
			s_injectedMarketGroups = new List<InvMarketGroups>
			{
				new InvMarketGroups
				{
					Name = "Unique Designs",
					Description = "Ships of a unique design",
					ID = DBConstants.UniqueDesignsRootNonMarketGroupID,
					ParentID = DBConstants.ShipsMarketGroupID,
					IconID = DBConstants.UnknownShipIconID
				},
				new InvMarketGroups
				{
					Name = "Various Non-Market",
					Description = "Non-Market Items",
					ID = DBConstants.RootNonMarketGroupID,
					ParentID = null,
					IconID = DBConstants.UnknownIconID
				}
			};

			// Add all items with null market group
			s_nullMarketItems = Database.InvTypesTable.Where(x => x.MarketGroupID == null).ToList();

			// Set some attributes to items because their MarketGroupID is NULL
			foreach (InvTypes srcItem in s_nullMarketItems)
			{
				// Set all items to market groups manually
				srcItem.MarketGroupID = DBConstants.RootNonMarketGroupID;

				// Set some ships market group and race
				switch (srcItem.ID)
				{
					case DBConstants.CapsuleID:
						srcItem.MarketGroupID = DBConstants.UniqueDesignsRootNonMarketGroupID;
						srcItem.RaceID = (int)Race.All;
						break;
				}
			}
		}

		/// <summary>
		/// Configures the non existing market group items.
		/// </summary>
		private static void ConfigureNonExistingMarketGroupItems()
		{
			var items = Database.InvTypesTable.Where(
				x => x.MarketGroupID != null && Database.InvMarketGroupsTable.All(y => y.ID != x.MarketGroupID)).ToList();

			foreach (var item in items)
			{
				item.MarketGroupID = DBConstants.RootNonMarketGroupID;
			}
		}

		/// <summary>
		/// Add properties to an item.
		/// </summary>
		/// <param name="srcItem"></param>
		/// <param name="groupItems"></param>
		/// <returns></returns>
		private static void CreateItem(InvTypes srcItem, ICollection<SerializableItem> groupItems)
		{
			Util.UpdatePercentDone(Database.ItemsTotalCount);

			srcItem.Generated = true;

			InvGroups itemGroup = Database.InvGroupsTable[srcItem.GroupID];

			// Creates the item with base informations
			SerializableItem item = new SerializableItem
			{
				ID = srcItem.ID,
				Name = srcItem.Name,
				Description = srcItem.Description ?? String.Empty,
				Icon = srcItem.IconID.HasValue
					? Database.EveIconsTable[srcItem.IconID.Value].Icon
					: String.Empty,
				PortionSize = srcItem.PortionSize,
				MetaGroup = ItemMetaGroup.None,
				Group = itemGroup.Name,
				Category = Database.InvCategoriesTable[itemGroup.CategoryID].Name,
				Race = (Race)Enum.ToObject(typeof(Race), srcItem.RaceID ?? 0)
			};


			// Set race to Faction if item race is Jovian or belongs to a Faction market group
			if (item.Race == Race.Jove ||
				Database.InvMarketGroupsTable.Any(group => srcItem.MarketGroupID == group.ID
														   && (DBConstants.FactionMarketGroupIDs.Any(id => id == group.ID) ||
															   DBConstants.FactionMarketGroupIDs.Any(id => id == group.ParentID))))
			{
				item.Race = Race.Faction;
			}

			// Add traits
			AddTraits(srcItem, item);

			// Add the properties and prereqs
			AddItemPropsAndPrereq(srcItem, item);

			// Metagroup
			AddMetaGroup(srcItem, item);

			// Look for slots
			if (Database.DgmTypeEffectsTable.Contains(srcItem.ID, 0, DBConstants.LowSlotEffectID))
				item.Slot = ItemSlot.Low;
			else if (Database.DgmTypeEffectsTable.Contains(srcItem.ID, 0, DBConstants.MedSlotEffectID))
				item.Slot = ItemSlot.Medium;
			else if (Database.DgmTypeEffectsTable.Contains(srcItem.ID, 0, DBConstants.HiSlotEffectID))
				item.Slot = ItemSlot.High;
			else
				item.Slot = ItemSlot.NoSlot;

			// Add reaction info for reactions
			if (Database.InvGroupsTable[srcItem.GroupID].CategoryID == DBConstants.ReactionCategoryID)
				AddReactionInfo(srcItem, item);

			// Add fuel info for control towers
			if (srcItem.GroupID == DBConstants.ControlTowerGroupID)
				AddControlTowerFuelInfo(srcItem, item);

			// Add this item
			groupItems.Add(item);

			// If the current item isn't in a market group then we are done
			if (srcItem.MarketGroupID == DBConstants.RootNonMarketGroupID)
				return;

			// Look for variations which are not in any market group
			foreach (InvTypes srcVariationItem in Database.InvMetaTypesTable.Where(x => x.ParentItemID == srcItem.ID).Select(
				variation => Database.InvTypesTable[variation.ItemID]).Where(
					srcVariationItem => srcVariationItem.Published &&
										srcVariationItem.MarketGroupID == DBConstants.RootNonMarketGroupID))
			{
				srcVariationItem.RaceID = (int)Race.Faction;
				CreateItem(srcVariationItem, groupItems);
			}
		}

		/// <summary>
		/// Adds the traits.
		/// </summary>
		/// <param name="srcItem">The source item.</param>
		/// <param name="item">The item.</param>
		private static void AddTraits(InvTypes srcItem, SerializableItem item)
		{
			if (Database.InvGroupsTable[srcItem.GroupID].CategoryID != DBConstants.ShipCategoryID)
				return;

			string skillBonusesText = String.Empty;
			string roleBonusesText = String.Empty;
			string miscBonusesText = String.Empty;

			// Find the skill bonuses
			skillBonusesText += $"{item.Name} bonuses (per skill level):{Environment.NewLine}";

			var SkillBonuses = Database.InvTraitsTable.Where(x => x.typeID == srcItem.ID && x.skillID != null && x.skillID >= 0);

			foreach (InvTraits bonuses in SkillBonuses)
			{
				skillBonusesText += bonuses.UnitID.HasValue
					? $"{bonuses.bonus}{Database.EveUnitsTable[bonuses.UnitID.Value].DisplayName} {bonuses.BonusText}{Environment.NewLine}"
					: $"{bonuses.BonusText}{Environment.NewLine}";
			}

			skillBonusesText += Environment.NewLine;

			// Find the role bonuses
			var RoleBonuses = Database.InvTraitsTable
				.Where(x => x.typeID == srcItem.ID && x.skillID == -1);

			roleBonusesText += $"Role bonus:{Environment.NewLine}";
			foreach (InvTraits bonuses in RoleBonuses)
			{
				roleBonusesText += bonuses.UnitID.HasValue
					? $"{bonuses.bonus}{Database.EveUnitsTable[bonuses.UnitID.Value].DisplayName} {bonuses.BonusText}{Environment.NewLine}"
					: $"{bonuses.BonusText}{Environment.NewLine}";
			}

			roleBonusesText += Environment.NewLine;

			// Find the misc bonuses
			var MiscBonuses = Database.InvTraitsTable
				.Where(x => x.typeID == srcItem.ID && x.skillID == -2);

			miscBonusesText += $"Misc bonus:{Environment.NewLine}";
			foreach (InvTraits bonuses in MiscBonuses)
			{
				miscBonusesText += bonuses.UnitID.HasValue
					? $"{bonuses.bonus}{Database.EveUnitsTable[bonuses.UnitID.Value].DisplayName} {bonuses.BonusText}{Environment.NewLine}"
					: $"{bonuses.BonusText}{Environment.NewLine}";
			}

			// For any T3 destoyer, we need to deal with CCP being horrific cheats. The 'ship traits' are actually derived through some epic hacking from some hidden items:
			// Hard coding some things in the short term, but need to make this MOAR BETTER.
			List<long> T3DIDs = new List<long> { 34562, 35683, 34317, 34828 };
			if (T3DIDs.Contains(item.ID))
			{
				Dictionary<string, int> T3DModeInfo = new Dictionary<string, int>();
				T3DModeInfo.Add("Sharpshooter", 0);
				T3DModeInfo.Add("Defense", 0);
				T3DModeInfo.Add("Propulsion", 0);

				// Determine which T3D we have, and get the relevant sub-item IDs
				switch (item.ID)
				{
					case 34562:
						// Svipul
						T3DModeInfo["Sharpshooter"] = 34570;
						T3DModeInfo["Propulsion"] = 34566;
						T3DModeInfo["Defense"] = 34564;
						break;
					case 35683:
						// Hecate
						T3DModeInfo["Sharpshooter"] = 35688;
						T3DModeInfo["Propulsion"] = 35687;
						T3DModeInfo["Defense"] = 35686;
						break;
					case 34317:
						// Confessor
						T3DModeInfo["Sharpshooter"] = 34321;
						T3DModeInfo["Propulsion"] = 34323;
						T3DModeInfo["Defense"] = 34319;
						break;
					case 34828:
						// Jackdaw
						T3DModeInfo["Sharpshooter"] = 35678;
						T3DModeInfo["Propulsion"] = 35677;
						T3DModeInfo["Defense"] = 35676;
						break;
					default:
						break;
				}

				foreach (var T3DMode in T3DModeInfo)
				{
					var DBRecord = Database.InvTypesTable[T3DMode.Value];
					miscBonusesText += $"{T3DMode.Key}{@" Mode:"}{Environment.NewLine}{DBRecord.Description}{Environment.NewLine + Environment.NewLine}";
				}
			}

			// Skip if no bonuses
			if (String.IsNullOrWhiteSpace(skillBonusesText) && String.IsNullOrWhiteSpace(roleBonusesText) &&
				String.IsNullOrWhiteSpace(miscBonusesText))
			{
				return;
			}

			StringBuilder sb = new StringBuilder();
			sb.AppendLine().AppendLine();
			sb.AppendLine("--- Traits --- ");
			sb.Append(SkillBonuses.Count() > 0 ? skillBonusesText : "");
			sb.Append(RoleBonuses.Count() > 0 ? roleBonusesText : "");
			sb.Append(MiscBonuses.Count() > 0 ? miscBonusesText : "");
	

			// Add to item description
			item.Description += sb.ToString();
		}

		/// <summary>
		/// Adds the control tower fuel info.
		/// </summary>
		/// <param name="srcItem">The source item.</param>
		/// <param name="item">The item.</param>
		private static void AddControlTowerFuelInfo(IHasID srcItem, SerializableItem item)
		{
			IEnumerable<SerializableControlTowerFuel> controlTowerResourcesTable = Database.InvControlTowerResourcesTable
				.Join(Database.InvControlTowerResourcePurposesTable, ctr => ctr.PurposeID, ctrp => ctrp.ID,
					(ctr, ctrp) => new { ctr, ctrp })
				.Where(x => x.ctr.ID == srcItem.ID)
				.Select(resource => new SerializableControlTowerFuel
				{
					ID = resource.ctr.ResourceID,
					Purpose = resource.ctrp.PurposeName,
					Quantity = resource.ctr.Quantity,
					MinSecurityLevel = resource.ctr.MinSecurityLevel.HasValue
						? resource.ctr.MinSecurityLevel.Value.ToString(CultureInfo.InvariantCulture)
						: String.Empty,
					FactionID = resource.ctr.FactionID.HasValue
						? resource.ctr.FactionID.Value.ToString(CultureInfo.InvariantCulture)
						: String.Empty,
					FactionName = resource.ctr.FactionID.HasValue
						? Database.ChrFactionsTable[resource.ctr.FactionID.Value].
							FactionName
						: String.Empty
				});

			item.ControlTowerFuelInfo.AddRange(controlTowerResourcesTable);
		}

		/// <summary>
		/// Adds the reaction info.
		/// </summary>
		/// <param name="srcItem">The source item.</param>
		/// <param name="item">The item.</param>
		private static void AddReactionInfo(IHasID srcItem, SerializableItem item)
		{
			item.ReactionInfo.AddRange(Database.InvTypeReactionsTable.Where(x => x.ID == srcItem.ID).Select(
				srcReaction => new
				{
					srcReaction,
					multiplier = Database.DgmTypeAttributesTable.Where(
						x => x.ItemID == srcReaction.TypeID &&
							 x.AttributeID == DBConstants.MoonMiningAmountPropertyID).Select(
								 x => x.GetInt64Value).FirstOrDefault()
				}).Select(
					reaction => new SerializableReactionInfo
					{
						ID = reaction.srcReaction.TypeID,
						IsInput = reaction.srcReaction.Input,
						Quantity = reaction.multiplier > 0
							? reaction.srcReaction.Quantity * reaction.multiplier
							: reaction.srcReaction.Quantity
					}));
		}

		/// <summary>
		/// Adds the meta group.
		/// </summary>
		/// <param name="srcItem">The source item.</param>
		/// <param name="item">The serializable item.</param>
		private static void AddMetaGroup(IHasID srcItem, SerializableItem item)
		{
			//int itemID = Database.InvBlueprintTypesTable.Any(x => x.ID == srcItem.ID)
			//    ? Database.InvBlueprintTypesTable[srcItem.ID].ProductTypeID
			//    : srcItem.ID;

			foreach (InvMetaTypes relation in Database.InvMetaTypesTable.Where(x => x.ItemID == srcItem.ID))
			{
				switch (relation.MetaGroupID)
				{
					case DBConstants.TechIMetaGroupID:
						item.MetaGroup = ItemMetaGroup.T1;
						break;
					case DBConstants.TechIIMetaGroupID:
						item.MetaGroup = ItemMetaGroup.T2;
						break;
					case DBConstants.StorylineMetaGroupID:
						item.MetaGroup = ItemMetaGroup.Storyline;
						break;
					case DBConstants.FactionMetaGroupID:
						item.MetaGroup = ItemMetaGroup.Faction;
						break;
					case DBConstants.OfficerMetaGroupID:
						item.MetaGroup = ItemMetaGroup.Officer;
						break;
					case DBConstants.DeadspaceMetaGroupID:
						item.MetaGroup = ItemMetaGroup.Deadspace;
						break;
					case DBConstants.TechIIIMetaGroupID:
						item.MetaGroup = ItemMetaGroup.T3;
						break;
					default:
						item.MetaGroup = ItemMetaGroup.None;
						break;
				}
			}

			if (item.MetaGroup == ItemMetaGroup.None)
				item.MetaGroup = ItemMetaGroup.T1;
		}

		/// <summary>
		/// Adds the item properties and prerequisites.
		/// </summary>
		/// <param name="srcItem">The source item.</param>
		/// <param name="item">The serializable item.</param>
		/// <returns></returns>
		private static void AddItemPropsAndPrereq(InvTypes srcItem, SerializableItem item)
		{
			Int64[] prereqSkills = new Int64[DBConstants.RequiredSkillPropertyIDs.Count];
			Int64[] prereqLevels = new Int64[DBConstants.RequiredSkillPropertyIDs.Count];
			List<SerializablePropertyValue> props = new List<SerializablePropertyValue>();
			double warpSpeedMultiplier = 1;
			foreach (DgmTypeAttributes srcProp in Database.DgmTypeAttributesTable.Where(x => x.ItemID == srcItem.ID))
			{
				Int64 propInt64Value = srcProp.GetInt64Value;

				// Is it a prereq skill ?
				int prereqIndex = DBConstants.RequiredSkillPropertyIDs.IndexOf(srcProp.AttributeID);
				if (prereqIndex > -1)
				{
					prereqSkills[prereqIndex] = propInt64Value;
					continue;
				}

				// Is it a prereq level ?
				prereqIndex = DBConstants.RequiredSkillLevelPropertyIDs.IndexOf(srcProp.AttributeID);
				if (prereqIndex > -1)
				{
					prereqLevels[prereqIndex] = propInt64Value;
					continue;
				}

				// Launcher group ?
				int launcherIndex = DBConstants.LauncherGroupPropertyIDs.IndexOf(srcProp.AttributeID);
				if (launcherIndex > -1)
				{
					props.Add(new SerializablePropertyValue
					{
						ID = srcProp.AttributeID,
						Value = Database.InvGroupsTable.HasValue(propInt64Value)
							? Database.InvGroupsTable[propInt64Value].Name
							: String.Empty
					});
					continue;
				}

				// Charge group ?
				int chargeIndex = DBConstants.ChargeGroupPropertyIDs.IndexOf(srcProp.AttributeID);
				if (chargeIndex > -1)
				{
					props.Add(new SerializablePropertyValue
					{
						ID = srcProp.AttributeID,
						Value = Database.InvGroupsTable.HasValue(propInt64Value)
							? Database.InvGroupsTable[propInt64Value].Name
							: String.Empty
					});
					continue;
				}

				// CanFitShip group ?
				int canFitShipIndex = DBConstants.CanFitShipGroupPropertyIDs.IndexOf(srcProp.AttributeID);
				if (canFitShipIndex > -1)
				{
					props.Add(new SerializablePropertyValue
					{
						ID = srcProp.AttributeID,
						Value = Database.InvGroupsTable.HasValue(propInt64Value)
							? Database.InvGroupsTable[propInt64Value].Name
							: String.Empty
					});
					continue;
				}

				// ModuleShip group ?
				int moduleShipIndex = DBConstants.ModuleShipGroupPropertyIDs.IndexOf(srcProp.AttributeID);
				if (moduleShipIndex > -1)
				{
					props.Add(new SerializablePropertyValue
					{
						ID = srcProp.AttributeID,
						Value = Database.InvGroupsTable.HasValue(propInt64Value)
							? Database.InvGroupsTable[propInt64Value].Name
							: String.Empty
					});
					continue;
				}

				// SpecialisationAsteroid group ?
				int specialisationAsteroidIndex = DBConstants.SpecialisationAsteroidGroupPropertyIDs.IndexOf(srcProp.AttributeID);
				if (specialisationAsteroidIndex > -1)
				{
					props.Add(new SerializablePropertyValue
					{
						ID = srcProp.AttributeID,
						Value = Database.InvGroupsTable.HasValue(propInt64Value)
							? Database.InvGroupsTable[propInt64Value].Name
							: String.Empty
					});
					continue;
				}

				// Reaction group ?
				int reactionIndex = DBConstants.ReactionGroupPropertyIDs.IndexOf(srcProp.AttributeID);
				if (reactionIndex > -1)
				{
					props.Add(new SerializablePropertyValue
					{
						ID = srcProp.AttributeID,
						Value = Database.InvGroupsTable.HasValue(propInt64Value)
							? Database.InvGroupsTable[propInt64Value].Name
							: String.Empty
					});
					continue;
				}

				// PosCargobayAccept group ?
				int posCargobayAcceptIndex = DBConstants.PosCargobayAcceptGroupPropertyIDs.IndexOf(srcProp.AttributeID);
				if (posCargobayAcceptIndex > -1)
				{
					props.Add(new SerializablePropertyValue
					{
						ID = srcProp.AttributeID,
						Value = Database.InvGroupsTable.HasValue(propInt64Value)
							? Database.InvGroupsTable[propInt64Value].Name
							: String.Empty
					});
					continue;
				}

				// Get the warp speed multiplier
				if (srcProp.AttributeID == DBConstants.WarpSpeedMultiplierPropertyID && srcProp.ValueFloat != null)
					warpSpeedMultiplier = srcProp.ValueFloat.Value;

				// We calculate and add the ships warp speed
				if (srcProp.AttributeID == DBConstants.ShipWarpSpeedPropertyID)
				{
					props.Add(new SerializablePropertyValue
					{
						ID = srcProp.AttributeID,
						Value = warpSpeedMultiplier.ToString(CultureConstants.InvariantCulture)
					});

					// Also add packaged volume as a prop as only ships have 'ship warp speed' attribute
					props.Add(new SerializablePropertyValue
					{
						ID = Properties.PackagedVolumePropertyID,
						Value = GetPackagedVolume(srcItem.GroupID).ToString(CultureConstants.InvariantCulture)
					});
				}

				// Other props
				props.Add(new SerializablePropertyValue { ID = srcProp.AttributeID, Value = srcProp.FormatPropertyValue() });

				AddMetaData(item, propInt64Value, srcProp);
			}

			CompleteItemPropertiesAddition(srcItem, props);

			// Add properties info to item
			item.Properties.AddRange(props);

			// Prerequisites completion
			List<SerializablePrerequisiteSkill> prereqs = new List<SerializablePrerequisiteSkill>();
			for (int i = 0; i < prereqSkills.Length; i++)
			{
				if (prereqSkills[i] != 0)
					prereqs.Add(new SerializablePrerequisiteSkill { ID = prereqSkills[i], Level = prereqLevels[i] });
			}

			// Add prerequisite skills info to item
			item.PrerequisiteSkills.AddRange(prereqs);
		}

		/// <summary>
		/// Adds the meta data.
		/// </summary>
		/// <param name="item">The item.</param>
		/// <param name="propInt64Value">The prop int value.</param>
		/// <param name="srcProp">The SRC prop.</param>
		private static void AddMetaData(SerializableItem item, Int64 propInt64Value, DgmTypeAttributes srcProp)
		{
			// Is metalevel property ?
			if (srcProp.AttributeID == DBConstants.MetaLevelPropertyID)
				item.MetaLevel = propInt64Value;

			// Is techlevel property ?
			if (srcProp.AttributeID == DBConstants.TechLevelPropertyID)
			{
				switch (propInt64Value)
				{
					default:
						item.MetaGroup = ItemMetaGroup.T1;
						break;
					case DBConstants.TechLevelII:
						item.MetaGroup = ItemMetaGroup.T2;
						break;
					case DBConstants.TechLevelIII:
						item.MetaGroup = ItemMetaGroup.T3;
						break;
				}
			}

			// Is metagroup property ?
			if (srcProp.AttributeID != DBConstants.MetaGroupPropertyID)
				return;

			switch (propInt64Value)
			{
				case DBConstants.StorylineMetaGroupID:
					item.MetaGroup = ItemMetaGroup.Storyline;
					break;
				case DBConstants.FactionMetaGroupID:
					item.MetaGroup = ItemMetaGroup.Faction;
					break;
				case DBConstants.OfficerMetaGroupID:
					item.MetaGroup = ItemMetaGroup.Officer;
					break;
				case DBConstants.DeadspaceMetaGroupID:
					item.MetaGroup = ItemMetaGroup.Deadspace;
					break;
				default:
					item.MetaGroup = ItemMetaGroup.None;
					break;
			}
		}

		/// <summary>
		/// Completes the item properties addition.
		/// </summary>
		/// <param name="srcItem">The SRC item.</param>
		/// <param name="props">The props.</param>
		private static void CompleteItemPropertiesAddition(InvTypes srcItem, ICollection<SerializablePropertyValue> props)
		{
			// Ensures there is a mass and add it to prop
			if (Math.Abs(srcItem.Mass) > double.Epsilon)
			{
				props.Add(new SerializablePropertyValue
				{
					ID = DBConstants.MassPropertyID,
					Value = srcItem.Mass.ToString(CultureConstants.InvariantCulture)
				});
			}

			// Ensures there is a cargo capacity and add it to prop
			if (Math.Abs(srcItem.Capacity) > double.Epsilon)
			{
				props.Add(new SerializablePropertyValue
				{
					ID = DBConstants.CargoCapacityPropertyID,
					Value = srcItem.Capacity.ToString(CultureConstants.InvariantCulture)
				});
			}

			// Ensures there is a volume and add it to prop
			if (Math.Abs(srcItem.Volume) > double.Epsilon)
			{
				props.Add(new SerializablePropertyValue
				{
					ID = DBConstants.VolumePropertyID,
					Value = srcItem.Volume.ToString(CultureConstants.InvariantCulture)
				});
			}

			// Add unit to refine prop where applicable
			if (Database.InvGroupsTable[srcItem.GroupID].CategoryID == DBConstants.AsteroidCategoryID)
			{
				props.Add(new SerializablePropertyValue
				{
					ID = Properties.UnitsToRefinePropertyID,
					Value = srcItem.PortionSize.ToString(CultureInfo.InvariantCulture)
				});
			}

			// Add base price as a prop
			props.Add(new SerializablePropertyValue
			{
				ID = Properties.BasePricePropertyID,
				Value = srcItem.BasePrice.FormatDecimal()
			});
		}

		/// <summary>
		/// Gets the packaged volume of a ship.
		/// </summary>
		/// <param name="groupID">The group ID.</param>
		/// <returns></returns>
		private static int GetPackagedVolume(int groupID)
		{
			switch (groupID)
			{
				default:
					return 500;
				case DBConstants.FrigateGroupID:
				case DBConstants.RookieShipGroupID:
				case DBConstants.AssaultFrigateGroupID:
				case DBConstants.CovertOpsGroupID:
				case DBConstants.InterceptorGroupID:
				case DBConstants.StealthBomberGroupID:
				case DBConstants.ElectronicAttackShipGroupID:
				case DBConstants.ExpeditionFrigateGroupID:
					return 2500;
				case DBConstants.MiningBargeGroupID:
				case DBConstants.ExhumerGroupID:
					return 3750;
				case DBConstants.DestroyerGroupID:
				case DBConstants.InterdictorGroupID:
				case DBConstants.StrategicCruiserGroupID:
				case DBConstants.TacticalDestroyerGroupID:
					return 5000;
				case DBConstants.CruiserGroupID:
				case DBConstants.HeavyAssaultCruiserGroupID:
				case DBConstants.LogisticsGroupID:
				case DBConstants.ForceReconShipGroupID:
				case DBConstants.HeavyInterdictorCruiserGroupID:
				case DBConstants.CombatReconShipGroupID:
					return 10000;
				case DBConstants.CombatBattlecruiserGroupID:
				case DBConstants.CommandShipGroupID:
				case DBConstants.AttackBattlecruiserGroupID:
					return 15000;
				case DBConstants.IndustrialGroupID:
				case DBConstants.DeepSpaceTransportGroupID:
				case DBConstants.BlockadeRunnerGroupID:
					return 20000;
				case DBConstants.BattleshipGroupID:
				case DBConstants.EliteBattleshipGroupID:
				case DBConstants.BlackOpsGroupID:
				case DBConstants.MarauderGroupID:
					return 50000;
				case DBConstants.IndustrialCommandShipGroupID:
					return 500000;
				case DBConstants.DreadnoughtGroupID:
				case DBConstants.FreighterGroupID:
				case DBConstants.CarrierGroupID:
				case DBConstants.SupercarrierGroupID:
				case DBConstants.CapitalIndustrialShipGroupID:
				case DBConstants.JumpFreighterGroupID:
					return 1000000;
				case DBConstants.TitanGroupID:
					return 10000000;
			}
		}

		/// <summary>
		/// Sets the item family according to its market group
		/// </summary>
		/// <param name="group"></param>
		/// <param name="itemFamily"></param>
		private static void SetItemFamilyByMarketGroup(SerializableMarketGroup group, ItemFamily itemFamily)
		{
			foreach (SerializableItem item in group.Items)
			{
				item.Family = itemFamily;
			}

			foreach (SerializableMarketGroup childGroup in group.SubGroups)
			{
				SetItemFamilyByMarketGroup(childGroup, itemFamily);
			}
		}
	}
}
