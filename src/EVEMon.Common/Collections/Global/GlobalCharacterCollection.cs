﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EVEMon.Common.Constants;
using EVEMon.Common.CustomEventArgs;
using EVEMon.Common.Extensions;
using EVEMon.Common.Models;
using EVEMon.Common.Serialization.Eve;
using EVEMon.Common.Serialization.Settings;

namespace EVEMon.Common.Collections.Global
{
    /// <summary>
    /// Represents the global collection of characters.
    /// </summary>
    public sealed class GlobalCharacterCollection : ReadonlyCollection<Character>
    {
        /// <summary>
        /// Gets a character by its guid.
        /// </summary>
        /// <param name="guid"></param>
        /// <returns></returns>
        public Character this[string guid] => Items.FirstOrDefault(character => character.Guid.ToString() == guid);

        /// <summary>
        /// Adds a character to this collection.
        /// </summary>
        /// <param name="character"></param>
        /// <param name="notify"></param>
        internal void Add(Character character, bool notify = true)
        {
            Items.Add(character);
            character.Monitored = true;

            // For CCP characters, also remove it from the API key's ignore list
            if (character is CCPCharacter)
                character.Identity.APIKeys.ToList().ForEach(apiKey => apiKey.IdentityIgnoreList.Remove(character.Identity));

            if (notify)
                EveMonClient.OnCharacterCollectionChanged();
        }

        /// <summary>
        /// Removes a character from this collection.
        /// Also removes it from the monitored characters collection,
        /// and assign it to the ignore list of its API key.
        /// </summary>
        /// <param name="character"></param>
        /// <param name="notify"></param>
        public void Remove(Character character, bool notify = true)
        {
            Items.Remove(character);
            character.Monitored = false;

            // For CCP characters, also add it on the API key's ignore list
            if (character is CCPCharacter)
                character.Identity.APIKeys.ToList().ForEach(apiKey => apiKey.IdentityIgnoreList.Add(character));

            // Dispose
            character.Dispose();

            if (notify)
                EveMonClient.OnCharacterCollectionChanged();
        }

        /// <summary>
        /// Asynchronously adds a character from the given uri, adding a new identity when needed.
        /// </summary>
        /// <param name="uri">The uri to load the character sheet from</param>
        /// <exception cref="System.ArgumentNullException">uri</exception>
        public static async Task<UriCharacterEventArgs> TryAddOrUpdateFromUriAsync(Uri uri)
        {
            uri.ThrowIfNull(nameof(uri));

            // It's a web address, let's do it in an async way
            if (!uri.IsFile)
            {
                CCPAPIResult<SerializableAPICharacterSheet> result =
                    await
                        Util.DownloadAPIResultAsync<SerializableAPICharacterSheet>(uri, false, null, APIProvider.RowsetsTransform);
                return new UriCharacterEventArgs(uri, result);
            }

            // We have a file, let's just deserialize it synchronously
            string xmlRootElement = Util.GetXmlRootElement(uri);

            switch (xmlRootElement.ToLower(CultureConstants.DefaultCulture))
            {
                case "eveapi":
                    CCPAPIResult<SerializableAPICharacterSheet> apiResult =
                        Util.DeserializeAPIResultFromFile<SerializableAPICharacterSheet>(uri.LocalPath,
                            APIProvider.RowsetsTransform);
                    return new UriCharacterEventArgs(uri, apiResult);
                case "serializableccpcharacter":
                    try
                    {
                        SerializableCCPCharacter ccpResult =
                            Util.DeserializeXmlFromFile<SerializableCCPCharacter>(uri.LocalPath);
                        return new UriCharacterEventArgs(uri, ccpResult);
                    }
                    catch (NullReferenceException ex)
                    {
                        return new UriCharacterEventArgs(uri,
                            $"Unable to load file (SerializableCCPCharacter). ({ex.Message})");
                    }
                case "serializableuricharacter":
                    try
                    {
                        SerializableUriCharacter uriCharacterResult =
                            Util.DeserializeXmlFromFile<SerializableUriCharacter>(uri.LocalPath);
                        return new UriCharacterEventArgs(uri, uriCharacterResult);
                    }
                    catch (NullReferenceException ex)
                    {
                        return new UriCharacterEventArgs(uri,
                            $"Unable to load file (SerializableUriCharacter). ({ex.Message})");
                    }
                default:
                    return new UriCharacterEventArgs(uri, "Format Not Recognized");
            }
        }

        /// <summary>
        /// Imports the character identities from a serialization object.
        /// </summary>
        /// <param name="serial"></param>
        internal void Import(IEnumerable<SerializableSettingsCharacter> serial)
        {
            // Clear the API key on every identity
            foreach (CharacterIdentity id in EveMonClient.CharacterIdentities)
            {
                id.APIKeys.Clear();
            }

            // Unsubscribe any event handlers in character
            foreach (Character character in Items)
            {
                character.Dispose();
            }

            // Import the characters, their identies, etc
            Items.Clear();
            foreach (SerializableSettingsCharacter serialCharacter in serial)
            {
                // Gets the identity or create it
                CharacterIdentity id = EveMonClient.CharacterIdentities[serialCharacter.ID] ??
                                       EveMonClient.CharacterIdentities.Add(serialCharacter.ID, serialCharacter.Name,
                                           serialCharacter.CorporationID, serialCharacter.CorporationName,
                                           serialCharacter.AllianceID, serialCharacter.AllianceName,
                                           serialCharacter.FactionID, serialCharacter.FactionName);

                // Imports the character
                SerializableCCPCharacter ccpCharacter = serialCharacter as SerializableCCPCharacter;
                if (ccpCharacter != null)
                    this.Add(new CCPCharacter(id, ccpCharacter));
                else
                {
                    SerializableUriCharacter uriCharacter = serialCharacter as SerializableUriCharacter;
                    this.Add(new UriCharacter(id, uriCharacter));
                }
            }

            // Notify the change
            EveMonClient.OnCharacterCollectionChanged();
        }

        /// <summary>
        /// Exports this collection to a serialization object.
        /// </summary>
        /// <returns></returns>
        internal IEnumerable<SerializableSettingsCharacter> Export() => Items.Select(character => character.Export());

        /// <summary>
        /// imports the plans from serialization objects.
        /// </summary>
        /// <param name="serial"></param>
        internal void ImportPlans(ICollection<SerializablePlan> serial)
        {
            foreach (Character character in Items)
            {
                character.ImportPlans(serial);
            }
        }

        /// <summary>
        /// Exports the plans as serialization objects.
        /// </summary>
        /// <returns></returns>
        internal IEnumerable<SerializablePlan> ExportPlans()
        {
            List<SerializablePlan> serial = new List<SerializablePlan>();
            foreach (Character character in Items)
            {
                character.ExportPlans(serial);
            }

            return serial;
        }

        /// <summary>
        /// Update character account statuses. Used after APIKeys list is updated
        /// </summary>
        internal void UpdateAccountStatuses()
        {
            foreach (Character character in Items)
            {
                character.UpdateAccountStatus();
            }
        }
    }
}