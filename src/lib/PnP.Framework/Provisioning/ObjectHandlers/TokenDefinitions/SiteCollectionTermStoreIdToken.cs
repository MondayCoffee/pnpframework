using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;
using PnP.Framework.Attributes;
using System.Linq;

namespace PnP.Framework.Provisioning.ObjectHandlers.TokenDefinitions
{
    [TokenDefinitionDescription(
        Token = "{sitecollectiontermstoreid}",
        Description = "Returns the id of the given default site collection term store",
        Example = "{sitecollectiontermstoreid}",
        Returns = "9188a794-cfcf-48b6-9ac5-df2048e8aa5d")]
    internal class SiteCollectionTermStoreIdToken : VolatileTokenDefinition
    {
        public SiteCollectionTermStoreIdToken(Web web)
            : base(web, "{sitecollectiontermstoreid}")
        {
        }

        public override string GetReplaceValue()
        {
            if (CacheValue == null)
            {
                TaxonomySession taxSession = TaxonomySession.GetTaxonomySession(TokenContext);
                TokenContext.Load(taxSession.TermStores, t => t.Include(t => t.Id, t => t.IsOnline));
                TokenContext.ExecuteQueryRetry();
                var termStore = taxSession.TermStores.FirstOrDefault(t => t.ServerObjectIsNull == false && t.IsOnline);
                //var termStore = session.GetDefaultSiteCollectionTermStore();
                //TokenContext.Load(termStore, t => t.Id);
                //TokenContext.ExecuteQueryRetry();
                if (termStore != null)
                {
                    CacheValue = termStore.Id.ToString();
                }
            }
            return CacheValue;
        }
    }
}