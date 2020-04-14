using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace k8s.Models
{
    /// <summary>
    ///     Describes object type in Kubernetes
    /// </summary>
    public sealed class KubernetesEntityAttribute : Attribute
    {
        /// <summary>
        /// The Kubernetes named schema this object is based on
        /// </summary>
        public string Kind { get; set; }

        /// <summary>
        /// The Group this Kubernetes type belongs to
        /// </summary>
        public string Group { get; set; }

        /// <summary>
        /// The API Version this Kubernetes type belongs to
        /// </summary>
        public string ApiVersion { get; set; }

        /// <summary>
        /// The plural name of the entity
        /// </summary>
        public string PluralName { get; set; }

        /// <summary>
        ///     The singular name of the entity
        /// </summary>
        public string SingularName { get; set; }

        public string[] Categories { get; set; }

        public string[] ShortNames { get; set; }

        public KubernetesEntityAttribute Validate()
        {
            if (ApiVersion == null)
            {
                throw new InvalidOperationException($"Custom resource must have {nameof(ApiVersion)} set");
            }

            if (PluralName == null)
            {
                throw new InvalidOperationException($"Custom resource must have {nameof(PluralName)} set");
            }

            if (PluralName.IsValidKubernetesName())
            {
                throw new InvalidOperationException($"{PluralName} is not a valid value for {nameof(PluralName)}");
            }

            if (SingularName != null && SingularName.IsValidKubernetesName())
            {
                throw new InvalidOperationException($"{SingularName} is not a valid value for  {nameof(SingularName)}");
            }

            if (!string.IsNullOrEmpty(Group) && !Regex.IsMatch(Group, @"^([a-zA-Z0-9-]+\.)+[a-zA-Z0-9-]+$"))
            {
                throw new InvalidOperationException($"{Group} is not a valid value for  {nameof(Group)}. Must have a hostname like format (ex. my.group.io)");
            }

            var invalidCategories = Categories?.Where(x => x.IsValidKubernetesName()).ToList() ?? new List<string>();
            if (invalidCategories.Any())
            {
                throw new InvalidOperationException($"{string.Join(", ", invalidCategories)} are not valid value(s) for {nameof(Categories)}");
            }

            var invalidShortNames = ShortNames?.Where(x => x.IsValidKubernetesName()).ToList() ?? new List<string>();
            if (invalidShortNames.Any())
            {
                throw new InvalidOperationException($"{string.Join(", ", invalidShortNames)} are not valid value(s) for {nameof(ShortNames)}");
            }

            return this;
        }
    }
}
