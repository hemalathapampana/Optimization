using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using System.Web.Mvc;
using System.Web.Services.Description;
using System.Windows.Media.Media3D;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amop.Core.Constants;
using KeySys.BaseMultiTenant.Helpers;
using KeySys.BaseMultiTenant.Helpers.Http;
using KeySys.BaseMultiTenant.Mapping;
using KeySys.BaseMultiTenant.Models.CustomClasses;
using KeySys.BaseMultiTenant.Models.Device;
using KeySys.BaseMultiTenant.Models.Optimization;
using KeySys.BaseMultiTenant.Models.Repositories;
using KeySys.BaseMultiTenant.Repositories.Optimization;
using KeySys.BaseMultiTenant.Resources;
using KeySys.BaseMultiTenant.Utilities;
using Microsoft.Ajax.Utilities;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

namespace KeySys.BaseMultiTenant.Controllers
{
    public class OptimizationController : AmopBaseController
    {
        private const string CARRIER_OPTIMIZATION_QUEUE_NAME = "Carrier Optimization Queue";
        private const string M2M_CUSTOMER_OPTIMIZATION_QUEUE_NAME = "Customer Optimization Queue";
        private const string MOBILITY_CUSTOMER_OPTIMIZATION_QUEUE_NAME = "Mobility Customer Optimization Queue";
        private const string CROSS_PROVIDER_CUSTOMER_OPTIMIZATION_QUEUE_NAME = "Cross Provider Customer Optimization Queue";
        private const string RATE_PLAN_QUEUE_NAME = "Rate Plan Queue";

        // GET: Optimization
        // Optimization Type 2 is for All
        public ActionResult Index(string filter = "", OptimizationType optimizationType = OptimizationType.All)
        {
            ViewBag.PageTitle = "Optimization";

            if (permissionManager.UserCannotAccess(Session, ModuleEnum.Optimization))
                return RedirectToAction("Index", "Home");

            var usesChargeModule = permissionManager.TenantHasModule(ModuleEnum.CustomerCharge);
            var model = new OptimizationListModel(altaWrxDb, permissionManager.AltaworxCentralConnectionString, permissionManager.Tenant.id, permissionManager.PermissionFilter.GetRevAccountFilter(), filter, (int)optimizationType, usesChargeModule);
            return View(model);
        }

        public ActionResult OptimizationGroup(string filter = "", OptimizationType optimizationType = OptimizationType.All)
        {
            ViewBag.PageTitle = "Optimization";

            if (permissionManager.UserCannotAccess(Session, ModuleEnum.Optimization))
                return RedirectToAction("Index", "Home");

            var model = new OptimizationSessionListModel(altaWrxDb, permissionManager.AltaworxCentralConnectionString, permissionManager.Tenant.id, filter, optimizationType);
            return View(model);
        }

        public ActionResult OptimizationInstancesBySession(int sessionId, int optimizationTypeId, int page = 1, string sort = "", string sortDir = "")
        {
            if (permissionManager.UserCannotAccess(Session, ModuleEnum.Optimization))
                return RedirectToAction("Index", "Home");

            var usesChargeModule = permissionManager.TenantHasModule(ModuleEnum.CustomerCharge);
            var siteType = SiteType.Rev;
            if (!permissionManager.UserCanAccess(Session, ModuleEnum.RevCustomers))
            {
                siteType = SiteType.AMOP;
            }
            var model = new OptimizationListModel(altaWrxDb, permissionManager.AltaworxCentralConnectionString, permissionManager.Tenant.id, permissionManager.PermissionFilter.GetRevAccountFilter(),
                string.Empty, optimizationTypeId, usesChargeModule, sessionId, page, 25, sort, sortDir, siteType);

            return PartialView("_OptimizationInstancesBySession", model);
        }

        public ActionResult Export(string filter = "", int optimizationType = (int)OptimizationType.All)
        {
            if (permissionManager.UserCannotAccess(Session, ModuleEnum.Optimization))
            {
                return RedirectToAction("Index", "Home");
            }

            var usesChargeModule = permissionManager.TenantHasModule(ModuleEnum.CustomerCharge);
            var model = new OptimizationListModel(altaWrxDb, permissionManager.AltaworxCentralConnectionString, permissionManager.Tenant.id, permissionManager.PermissionFilter.GetRevAccountFilter(),
                filter, optimizationType, usesChargeModule);

            var lineItems = model.Optimizations.Where(oi => oi.OptimizationSessionId != null)
                .Select(oi => oi.ToOptimizationExport()).ToList();

            var data = lineItems.ToDataSet("Optimization List");
            var bytes = ExcelUtilities.Export(data);

            Zipper zipper = new Zipper();
            zipper.AddFile(new MemoryStream(bytes), string.Empty, string.Format("Optimization_{0}{1}{2}{3}{4}.xlsx",
                DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, DateTime.UtcNow.Minute));

            var sessions = model.Optimizations.Select(os => os.SessionId).Distinct();
            sessions.ForEach(session =>
            {
                zipper.AddFolder(session.ToString());
                zipper.AddFolder($"{session}\\Rate Plan Assignments");
            });

            model.Optimizations.ForEach(optimization =>
            {
                var results = altaWrxDb.OptimizationInstanceResultFiles.Find(optimization.ResultsId);
                if (results != null)
                {
                    byte[] assignmentXlsFile = results.AssignmentXlsxBytes;
                    zipper.AddFile(new MemoryStream(assignmentXlsFile), $"{optimization.SessionId}\\Rate Plan Assignments", $"{optimization.CustomerName}_device_assignments_{results.InstanceId}.xlsx");
                }
            });

            byte[] zipData = zipper.GetStream();
            return File(zipData, "application/octet-stream", string.Format("Optimization_{0}{1}{2}{3}{4}.zip",
                DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, DateTime.UtcNow.Minute));
        }

        public ActionResult DownloadSessionResults(int sessionId, int optimizationTypeId = (int)OptimizationType.All)
        {
            if (permissionManager.UserCannotAccess(Session, ModuleEnum.Optimization))
            {
                return RedirectToAction("Index", "Home");
            }

            var usesChargeModule = permissionManager.TenantHasModule(ModuleEnum.CustomerCharge);
            var siteType = SiteType.Rev;
            if (!permissionManager.UserCanAccess(Session, ModuleEnum.RevCustomers))
            {
                siteType = SiteType.AMOP;
            }
            var model = new OptimizationListModel(altaWrxDb, permissionManager.AltaworxCentralConnectionString, permissionManager.Tenant.id, permissionManager.PermissionFilter.GetRevAccountFilter(),
                string.Empty, optimizationTypeId, usesChargeModule, sessionId, 1, int.MaxValue, string.Empty, string.Empty, siteType, false);

            var lineItems = model.Optimizations.Where(oi => oi.OptimizationSessionId != null && oi.ResultsId != null)
                .Select(oi => oi.ToOptimizationExport()).ToList();

            //list of optimization instances
            var data = lineItems.ToDataSet("Optimization List");
            var bytes = ExcelUtilities.Export(data);

            Zipper zipper = new Zipper();
            zipper.AddFile(new MemoryStream(bytes), string.Empty, string.Format("OptimizationSession_{0}{1}{2}{3}{4}.xlsx",
                DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, DateTime.UtcNow.Minute));

            //rate plan assignment of each optimization instance
            zipper.AddFolder($"Rate Plan Assignments");

            model.Optimizations.ForEach(optimization =>
            {
                var results = altaWrxDb.OptimizationInstanceResultFiles.Find(optimization.ResultsId);
                if (results != null)
                {
                    byte[] assignmentXlsFile = results.AssignmentXlsxBytes;
                    zipper.AddFile(new MemoryStream(assignmentXlsFile), $"Rate Plan Assignments", $"{optimization.CustomerName.Trim()}_device_assignments_{results.InstanceId}.xlsx");
                }
            });

            byte[] zipData = zipper.GetStream();
            return File(zipData, "application/octet-stream", string.Format("OptimizationSession_{0}{1}{2}{3}{4}.zip",
                DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, DateTime.UtcNow.Minute));
        }

        public ActionResult _Start(PortalTypes? portalType, string optimizationFrom = "", bool isCrossProviderOptimization = false)
        {
            if (!permissionManager.UserCanCreate(Session, ModuleEnum.Optimization))
            {
                return new EmptyResult();
            }

            ViewBag.ServiceProviders = portalType != null
                ? ListHelper.ServiceProviderList(altaWrxDb, null, isCrossProviderOptimization, (PortalTypes)portalType)
                : ListHelper.ServiceProviderList(altaWrxDb, null, isCrossProviderOptimization, PortalTypes.M2M, PortalTypes.Mobility);

            var siteType = SiteType.Rev;
            if (!permissionManager.UserCanAccess(Session, ModuleEnum.RevCustomers))
            {
                siteType = SiteType.AMOP;
            }

            var optimizationStart = new OptimizationStart
            {
                OptimizationFrom = optimizationFrom,
                SiteType = siteType
            };

            return PartialView(optimizationStart);
        }

        public ActionResult QueueRatePlanChanges(long id)
        {
            ViewBag.PageTitle = "Rate Plan Upload";

            if (!permissionManager.UserCanCreate(Session, ModuleEnum.Optimization) ||
                (!permissionManager.UserIsSuperAdmin(Session) && !permissionManager.UserIsTenantAdmin(Session)))
                return RedirectToAction("Index", "Home");

            var model = new OptimizationRatePlanQueueModel(altaWrxDb, id);
            return View(model);
        }

        public async Task<ActionResult> QueueRatePlanChangesConfirm(long id)
        {
            ViewBag.PageTitle = "Rate Plan Upload";

            if (!permissionManager.UserCanCreate(Session, ModuleEnum.Optimization))
                return RedirectToAction("Index", "Home");

            // get tenant custom fields
            var customObjectDbList = permissionManager.CustomFields;
            string awsAccessKey = AwsAccessKeyFromCustomObjects(customObjectDbList);
            string awsSecretAccessKey = AwsSecretAccessKeyFromCustomObjects(customObjectDbList);
            var optimizationInstance = altaWrxDb.OptimizationInstances.FirstOrDefault(x => x.Id == id);
            if (!CheckExistRatePlanAdjustmentInBillingPeriod(id, optimizationInstance?.ServiceProvider))
            {
                if (permissionManager.UserIsSuperAdmin(Session) || permissionManager.UserIsTenantAdmin(Session))
                {
                    string ratePlanQueueName = RatePlanQueueFromCustomObjects(customObjectDbList);

                    var errorMessage = await EnqueueRatePlanSqsAsync(id, awsAccessKey, awsSecretAccessKey, ratePlanQueueName, optimizationInstance?.ServiceProvider);
                    if (!string.IsNullOrEmpty(errorMessage))
                    {
                        SessionHelper.SetAlert(Session, errorMessage);
                        SessionHelper.SetAlertType(Session, CommonConstants.DANGER);
                    }
                    else
                    {
                        SessionHelper.SetAlert(Session, string.Format(LogCommonStrings.SUCCESSFULLY_STARTED_RATE_PLAN_CHANGES, CommonConstants.DEFAULT_LAMBDA_INSTANCE_REMAINING_SECONDS_LIMIT));
                        SessionHelper.SetAlertType(Session, CommonConstants.SUCCESS);

                        return RedirectToAction("RatePlanConfirm", new { id = id });
                    }
                }
                else
                {
                    SessionHelper.SetAlert(Session, LogCommonStrings.INSUFFICIENT_PRIVILEGES_TO_QUEUE_RATE_PLAN_CHANGES);
                    SessionHelper.SetAlertType(Session, CommonConstants.DANGER);
                }
            }
            else
            {
                SessionHelper.SetAlert(Session, LogCommonStrings.CHANGES_HAVE_ALREADY_BEEN_PUSHED_FOR_THIS_BILL_PERIOD);
                SessionHelper.SetAlertType(Session, CommonConstants.DANGER);
                return RedirectToAction("QueueRatePlanChanges", new { id = id });
            }

            return RedirectToAction("Index");
        }

        public ActionResult RatePlanConfirm(long id)
        {
            ViewBag.PageTitle = "Rate Plan Upload";

            if (!permissionManager.UserCanCreate(Session, ModuleEnum.Optimization))
            {
                return RedirectToAction("Index", "Home");
            }

            var model = new RatePlanConfirmViewModel(altaWrxDb, id);
            return View(model);
        }

        public ActionResult Start(string billPeriod, string customerBillPeriod, string serviceProviderIds, string siteIdString, OptimizationType optimizationType, string optimizationFrom = "", string billingEndDayList = "")
        {
            ViewBag.PageTitle = "Optimization Start";

            int? siteId = null;
            if (!string.IsNullOrEmpty(siteIdString))
            {
                var parsedSiteId = int.Parse(siteIdString);
                if (parsedSiteId > 0)
                {
                    siteId = parsedSiteId;
                }
            }

            if (!permissionManager.UserCanCreate(Session, ModuleEnum.Optimization))
            {
                return RedirectToAction("Index", "Home");
            }
            BillingPeriod bp = new BillingPeriod();
            CustomerBillingPeriod customerBillingPeriod = new CustomerBillingPeriod();

            var siteType = SiteType.Rev;
            if (!permissionManager.UserCanAccess(Session, ModuleEnum.RevCustomers))
            {
                siteType = SiteType.AMOP;
            }

            using (AltaWorxCentral_Entities db = new AltaWorxCentral_Entities(permissionManager.AltaworxCentralConnectionString))
            {
                if (optimizationType == OptimizationType.Carrier || !permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization)
                {
                    bp = db.BillingPeriods.Find(int.Parse(billPeriod));
                }
                else
                {
                    customerBillingPeriod = db.CustomerBillingPeriods.Find(int.Parse(customerBillPeriod));
                }
            }

            if ((bp == null && !permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization) ||
                (customerBillingPeriod == null && permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization))
            {
                SessionHelper.SetAlert(Session, LogCommonStrings.INVALID_BILLING_PERIOD);
                SessionHelper.SetAlertType(Session, CommonConstants.ALERT_TYPE_DANGER);
                return View(new OptimizationStartModel());
            }
            OptimizationStartModel model;
            if (optimizationType == OptimizationType.Carrier || !permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization)
            {
                model = new OptimizationStartModel(altaWrxDb, permissionManager.AltaworxCentralConnectionString, bp, int.Parse(serviceProviderIds), siteId, permissionManager.Tenant.id, optimizationType, permissionManager, optimizationFrom, siteType);
            }
            else
            {
                var serviceProviders = new List<int>();
                if (serviceProviderIds != null)
                {
                    serviceProviders = JsonConvert.DeserializeObject<List<int>>(serviceProviderIds);
                }
                model = new OptimizationStartModel(altaWrxDb, permissionManager.AltaworxCentralConnectionString, bp, customerBillingPeriod, serviceProviders, siteId, permissionManager.Tenant.id, optimizationType, permissionManager, billingEndDayList, optimizationFrom, siteType);
            }

            if (!model.CanStart)
            {
                SessionHelper.SetAlert(Session, model.ValidationErrorMessage);
                SessionHelper.SetAlertType(Session, "danger");
            }

            return View(model);
        }

        public async Task<ActionResult> StartConfirm(int billPeriodId, int? siteId, int? serviceProviderId, string serviceProviderIds, OptimizationType optimizationType, string optimizationFrom = "", string billingEndDayList = "")
        {
            ViewBag.PageTitle = "Optimization Started";
            if (!permissionManager.UserCanCreate(Session, ModuleEnum.Optimization))
                return RedirectToAction("Index", "Home");

            var siteType = SiteType.Rev;
            if (!permissionManager.UserCanAccess(Session, ModuleEnum.RevCustomers))
            {
                siteType = SiteType.AMOP;
            }
            // get tenant custom fields
            var customObjectDbList = permissionManager.CustomFields;
            string awsAccessKey = AwsAccessKeyFromCustomObjects(customObjectDbList);
            string awsSecretAccessKey = AwsSecretAccessKeyFromCustomObjects(customObjectDbList);
            string errorMessage;

            BillingPeriod billPeriod = new BillingPeriod();
            CustomerBillingPeriod customerBillPeriod = new CustomerBillingPeriod();
            long optimizationSessionId = 0;
            string serviceProvidersString = string.Empty;
            List<DateTime> endDateList = new List<DateTime>();
            if (optimizationType == OptimizationType.Carrier || !permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization)
            {
                billPeriod = altaWrxDb.BillingPeriods.AsNoTracking().Include(x => x.ServiceProvider.Integration).FirstOrDefault(x => x.id == billPeriodId);
                if (billPeriod != null)
                {
                    optimizationSessionId = await CreateOptimizationSession(billPeriod.BillingCycleStartDate, billPeriod.BillingCycleEndDate, serviceProviderId, null, siteId, optimizationType);
                }
            }
            else
            {
                customerBillPeriod = altaWrxDb.CustomerBillingPeriods.AsNoTracking().FirstOrDefault(x => x.id == billPeriodId);
                var site = altaWrxDb.Sites.AsNoTracking().FirstOrDefault(x => x.id == siteId);
                DateTime billingCycleEndDate;
                DateTime billingCycleStartDate;
                if (site == null)
                {
                    var endDayList = billingEndDayList.Split(',').ToList();
                    endDateList = endDayList.Select(x => new DateTime(customerBillPeriod.BillYear, customerBillPeriod.BillMonth,
                        int.Parse(x.Split('/')[0]))).OrderBy(x => x).ToList();
                    billingCycleEndDate = endDateList[endDateList.Count - 1];
                    billingCycleStartDate = endDateList[0].AddMonths(-1);
                }
                else
                {
                    billingCycleEndDate = new DateTime(customerBillPeriod?.BillYear ?? 18, customerBillPeriod?.BillMonth ?? 18, site?.CustomerBillPeriodEndDay ?? 0, site?.CustomerBillPeriodEndHour.Value ?? 0, 0, 0);
                    billingCycleStartDate = billingCycleEndDate.AddMonths(-1);
                }
                var serviceProviders = new List<int>();
                if (serviceProviderIds != null)
                {
                    serviceProviders = JsonConvert.DeserializeObject<List<int>>(serviceProviderIds);
                }
                serviceProvidersString = string.Join(",", serviceProviders);
                if (billPeriod != null)
                {
                    optimizationSessionId = await CreateOptimizationSession(billingCycleStartDate, billingCycleEndDate, serviceProviderId, serviceProvidersString, siteId, optimizationType);
                }
            }

            if (optimizationSessionId == 0)
                errorMessage = "Error creating Optimization Session. Please contact AMOP Support.";
            else
            {
                switch (optimizationType)
                {
                    case OptimizationType.Customer:
                        errorMessage = await EnqueueCustomerOptimizationAsync(billPeriod, customerBillPeriod, siteId, serviceProviderId, serviceProvidersString, customObjectDbList,
                            awsAccessKey, awsSecretAccessKey, optimizationSessionId, siteType, endDateList);
                        break;
                    case OptimizationType.Carrier:
                        errorMessage = await EnqueueCarrierOptimizationAsync(billPeriod, serviceProviderId, customObjectDbList, awsAccessKey,
                            awsSecretAccessKey, optimizationSessionId);
                        break;
                    default:
                        errorMessage = $"Unhandled optimization type: {optimizationType}";
                        break;
                }
            }

            if (string.IsNullOrEmpty(errorMessage))
            {
                var delay = optimizationType == OptimizationType.Carrier ? "within 10 minutes, after syncing most recent usage" : "within 60 seconds";
                var successMessage = $"Successfully started optimization should appear in the list {delay}. Please allow ample time for the optimization to complete. Larger jobs can take >30 minutes to process.";
                SessionHelper.SetAlert(Session, successMessage);
                SessionHelper.SetAlertType(Session, "success");
            }
            else
            {
                SessionHelper.SetAlert(Session, errorMessage);
                SessionHelper.SetAlertType(Session, "danger");
            }

            return optimizationFrom == "group" ? RedirectToAction("OptimizationGroup") : RedirectToAction("Index");
        }

        private async Task<long> CreateOptimizationSession(DateTime? billPeriodBillingCycleStartDate, DateTime? billPeriodBillingCycleEndDate, int? serviceProviderId, string serviceProviderIds, int? siteId, OptimizationType optimizationType)
        {
            var optimizationSesstionRepository = new OptimizationSessionRepository(altaWrxDb);
            var optimizationSession = new OptimizationSession
            {
                SessionId = Guid.NewGuid(),
                BillingPeriodStartDate = billPeriodBillingCycleStartDate.GetValueOrDefault(),
                BillingPeriodEndDate = billPeriodBillingCycleEndDate.GetValueOrDefault(),
                TenantId = permissionManager.Tenant.id,
                ServiceProviderId = serviceProviderId,
                ServiceProviderIds = serviceProviderIds,
                SiteId = siteId,
                OptimizationTypeId = (int)optimizationType,
                CreatedBy = SessionHelper.GetAuditByName(Session),
                CreatedDate = DateTime.UtcNow,
                IsActive = true,
                IsDeleted = false
            };
            await optimizationSesstionRepository.CreateOptimizationSession(optimizationSession);
            return optimizationSession.Id;
        }

        private async Task<string> EnqueueCustomerOptimizationAsync(BillingPeriod billPeriod, CustomerBillingPeriod customerBillPeriod, int? siteId, int? serviceProviderId, string serviceProviderIds, IList<CustomObject> customObjectDbList,
            string awsAccessKey, string awsSecretAccessKey, long optimizationSessionId, SiteType siteType, List<DateTime> endDateList)
        {
            var hasCustomer = siteId.HasValue;
            if (!hasCustomer && !serviceProviderId.HasValue && !permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization)
            {
                return "Service Provider or Customer must be supplied to start.";
            }

            var siteFilter = permissionManager.PermissionFilter.GetSiteIdFilter();
            if (!permissionManager.UserIsSuperAdmin(Session) && !permissionManager.UserIsTenantAdmin(Session) && siteFilter.IsRestricted && siteId.HasValue && !siteFilter.FilterValues.Contains(siteId.Value))
            {
                return "Insufficient privileges to queue optimization for customer.";
            }

            string m2mCustomerOptimizationQueueName = M2MCustomerOptimizationQueueFromCustomObjects(customObjectDbList);
            string mobilityCustomerOptimizationQueueName = MobilityCustomerOptimizationQueueFromCustomObjects(customObjectDbList);
            string crossProviderCustomerOptimizationQueueName = CrossProviderCustomerOptimizationQueueFromCustomObjects(customObjectDbList);

            if (hasCustomer)
            {
                return await EnqueueSingleCustomerOptimizationAsync(billPeriod, customerBillPeriod, permissionManager.Tenant.id, siteId.Value, serviceProviderId, serviceProviderIds, awsAccessKey,
                    awsSecretAccessKey, m2mCustomerOptimizationQueueName, mobilityCustomerOptimizationQueueName, crossProviderCustomerOptimizationQueueName, optimizationSessionId, siteType);
            }

            var serviceProvider = altaWrxDb.ServiceProviders.Include(sp => sp.Integration).FirstOrDefault(sp => sp.id == serviceProviderId);
            if (serviceProvider == null && !permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization)
            {
                return "Service Provider not found.";
            }

            PortalTypes portalType;
            if (permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization)
            {
                portalType = PortalTypes.CrossProvider;
            }
            else
            {
                portalType = (PortalTypes)serviceProvider.Integration.PortalTypeId;
            }
            switch (portalType)
            {
                case PortalTypes.M2M:
                    return await EnqueueAllCustomersOptimizationAsync(billPeriod, permissionManager.Tenant.id, serviceProvider,
                        awsAccessKey, awsSecretAccessKey, m2mCustomerOptimizationQueueName, optimizationSessionId, siteType);
                case PortalTypes.Mobility:
                    return await EnqueueAllCustomersOptimizationAsync(billPeriod, permissionManager.Tenant.id, serviceProvider,
                        awsAccessKey, awsSecretAccessKey, mobilityCustomerOptimizationQueueName,
                        optimizationSessionId, siteType);
                case PortalTypes.CrossProvider:
                    return await EnqueueCrossAllCustomersOptimizationAsync(customerBillPeriod, permissionManager.Tenant.id, serviceProviderIds,
                        awsAccessKey, awsSecretAccessKey, crossProviderCustomerOptimizationQueueName, optimizationSessionId, siteType, endDateList);
                default:
                    return $"Unhandled portal type: {portalType}";
            }
        }

        private async Task<string> EnqueueSingleCustomerOptimizationAsync(BillingPeriod billPeriod, CustomerBillingPeriod customerBillPeriod, int tenantId, int siteId,
            int? serviceProviderId, string serviceProviderIds, string awsAccessKey, string awsSecretAccessKey, string m2mCustomerOptimizationQueueName,
            string mobilityCustomerOptimizationQueueName, string crossProviderCustomerOptimizationQueueName, long optimizationSessionId, SiteType siteType)
        {
            var site = altaWrxDb.Sites.Include(s => s.RevCustomer).FirstOrDefault(x => x.id == siteId);
            if (site == null)
            {
                return "Customer not found.";
            }

            if (siteType == SiteType.Rev && site.RevCustomer != null && site.RevCustomer?.IntegrationAuthenticationId == null)
            {
                return "No valid billing provider credentials found for customer.";
            }
            var revCustId = string.Empty;
            int? integrationAuthId = null;
            var optCus = new OptimizationCustomerProcessing()
            {
                StartTime = DateTime.UtcNow,
                IsProcessed = false,
                ServiceProviderId = serviceProviderId,
                SessionId = optimizationSessionId
            };
            if (siteType == SiteType.Rev)
            {
                revCustId = site.RevCustomer.id.ToString();
                integrationAuthId = site.RevCustomer.IntegrationAuthenticationId.Value;

                optCus.CustomerId = site.RevCustomer.RevCustomerId;
                optCus.CustomerName = $"{site.RevCustomer.CustomerName} ({site.RevCustomer.RevCustomerId})";
            }
            else
            {
                optCus.AMOPCustomerId = site.id;
                optCus.AMOPCustomerName = site.Name;
            }
            altaWrxDb.OptimizationCustomerProcessings.Add(optCus);
            altaWrxDb.SaveChanges();

            return await EnqueueCustomerOptimizationSqsAsync(billPeriod, customerBillPeriod, tenantId, site.id,
                integrationAuthId, serviceProviderId, serviceProviderIds, awsAccessKey, awsSecretAccessKey,
                m2mCustomerOptimizationQueueName, mobilityCustomerOptimizationQueueName, crossProviderCustomerOptimizationQueueName, revCustId,
                optimizationSessionId, siteType);
        }

        private async Task<string> EnqueueAllCustomersOptimizationAsync(BillingPeriod billPeriod, int tenantId, ServiceProvider serviceProvider,
            string awsAccessKey, string awsSecretAccessKey, string customerOptimizationQueueName, long optimizationSessionId, SiteType siteType)
        {
            //var serviceProvider = altaWrxDb.ServiceProviders.Include(sp => sp.Integration).FirstOrDefault(sp => sp.id == serviceProvider.id);
            var serviceProviderId = serviceProvider.id;
            if (serviceProvider == null)
            {
                return "Service provider not found";
            }

            var portalType = (PortalTypes)serviceProvider.Integration.PortalTypeId;
            var dateHelper = new DateHelper(altaWrxDb, serviceProviderId, billPeriod.BillYear, billPeriod.BillMonth);
            var billingPeriodEnd = dateHelper.BillingPeriodEnd(billPeriod);
            var customers = GetOptimizationCustomers(tenantId, serviceProviderId, billPeriod.id, portalType, null, null, null);
            var amopCustomers = GetOptimizationAMOPCustomers(tenantId, serviceProviderId, billPeriod.id, portalType);
            if (!customers.Any() && !amopCustomers.Any())
            {
                return "No customers found with eligible SIMs";
            }

            try
            {
                var optimizationSessionRepository = new OptimizationSessionRepository(altaWrxDb);
                var awsCredentials = new Amazon.Runtime.BasicAWSCredentials(awsAccessKey, awsSecretAccessKey);
                using (var client = new AmazonSQSClient(awsCredentials, Amazon.RegionEndpoint.USEast1))
                {
                    var queueList = client.ListQueues(customerOptimizationQueueName);
                    if (queueList.HttpStatusCode != System.Net.HttpStatusCode.OK || queueList.QueueUrls == null || queueList.QueueUrls.Count <= 0)
                    {
                        return "Error Queuing Customer Optimization: Queue not found";
                    }

                    var queueUrl = queueList.QueueUrls[0];
                    var tasks = new List<Task<string>>();

                    if (siteType == SiteType.Rev)
                    {
                        for (var i = 0; i < customers.Count; i++)
                        {
                            var customer = customers[i];
                            var delaySeconds = Math.Min(i * 2, 900); // Prevent DoS'ing the database with a flood of downstream connections
                            var isLastInstance = false;
                            if (i == customers.Count - 1)
                                isLastInstance = true;
                            tasks.Add(EnqueueCustomerOptimizationSqsAsync(client, queueUrl, tenantId, customer.AmopCustomerId.ToString(),
                                customer.RevIntegrationAuthId.GetValueOrDefault(), serviceProviderId, billPeriod.BillYear, billPeriod.BillMonth,
                                billPeriod.id, optimizationSessionId, null, isLastInstance, siteType, delaySeconds));

                            // insert to optimization customer processing table
                            var optCus = new OptimizationCustomerProcessing()
                            {
                                CustomerId = customer.RevCustomerId,
                                StartTime = DateTime.UtcNow,
                                IsProcessed = false,
                                CustomerName = $"{customer.RevCustomerName} ({customer.RevCustomerId})",
                                ServiceProviderId = serviceProvider.id,
                                SessionId = optimizationSessionId
                            };
                            altaWrxDb.OptimizationCustomerProcessings.Add(optCus);
                            optimizationSessionRepository.AddOptimizationInstanceToBeProcessed(optimizationSessionId, customer.AmopCustomerId.ToString(), null);
                        }
                        altaWrxDb.SaveChanges();
                    }

                    if (siteType == SiteType.AMOP)
                    {
                        for (var i = 0; i < amopCustomers.Count; i++)
                        {
                            var amopCustomer = amopCustomers[i];
                            var delaySeconds = Math.Min(i * 2, 900); // Prevent DoS'ing the database with a flood of downstream connections
                            var isLastInstance = false;
                            if (i == amopCustomers.Count - 1)
                                isLastInstance = true;
                            tasks.Add(EnqueueCustomerOptimizationSqsAsync(client, queueUrl, tenantId, null,
                                null, serviceProviderId, billPeriod.BillYear, billPeriod.BillMonth,
                                billPeriod.id, optimizationSessionId, amopCustomer.SiteId, isLastInstance, siteType, delaySeconds));

                            // insert to optimization customer processing table
                            var optCus = new OptimizationCustomerProcessing()
                            {
                                AMOPCustomerId = amopCustomer.SiteId,
                                AMOPCustomerName = amopCustomer.SiteName,
                                StartTime = DateTime.UtcNow,
                                IsProcessed = false,
                                ServiceProviderId = serviceProvider.id,
                                SessionId = optimizationSessionId
                            };
                            altaWrxDb.OptimizationCustomerProcessings.Add(optCus);
                            optimizationSessionRepository.AddOptimizationInstanceToBeProcessed(optimizationSessionId, null, amopCustomer.SiteId);
                        }
                        altaWrxDb.SaveChanges();
                    }

                    var results = await Task.WhenAll(tasks);
                    var errorMessage = string.Join(Environment.NewLine, results.Where(result => !string.IsNullOrWhiteSpace(result)));
                    SendAllCustomersOptimizationSummaryEmail(serviceProviderId, billingPeriodEnd, customers, errorMessage);
                    return errorMessage;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error Queuing Customer Optimization for All Customers", ex);
                return "Error Queuing Customer Optimization: Exception occured";
            }
        }

        private async Task<string> EnqueueCrossAllCustomersOptimizationAsync(CustomerBillingPeriod billPeriod, int tenantId, string serviceProviderIds,
            string awsAccessKey, string awsSecretAccessKey, string customerOptimizationQueueName, long optimizationSessionId, SiteType siteType, List<DateTime> endDateList)
        {
            var portalType = PortalTypes.CrossProvider;
            var billingPeriodEnd = endDateList[endDateList.Count - 1];
            var billingPeriodStart = endDateList[0].AddMonths(-1);
            var allCustomer = GetCrossCustomerOptimization(billPeriod, tenantId, serviceProviderIds, endDateList, billingPeriodStart, billingPeriodEnd);
            var revCustomers = allCustomer.Where(x => x.RevCustomerId != null && x.RevIntegrationAuthId != null).ToList();
            var amopCustomers = allCustomer.Where(x => x.RevCustomerId == null || x.RevIntegrationAuthId == null).ToList();
            if (!revCustomers.Any() && !amopCustomers.Any())
            {
                return "No customers found with eligible SIMs";
            }

            try
            {
                var optimizationSessionRepository = new OptimizationSessionRepository(altaWrxDb);
                var awsCredentials = new Amazon.Runtime.BasicAWSCredentials(awsAccessKey, awsSecretAccessKey);
                using (var client = new AmazonSQSClient(awsCredentials, Amazon.RegionEndpoint.USEast1))
                {
                    var queueList = client.ListQueues(customerOptimizationQueueName);
                    if (queueList.HttpStatusCode != System.Net.HttpStatusCode.OK || queueList.QueueUrls == null || queueList.QueueUrls.Count <= 0)
                    {
                        return "Error Queuing Customer Optimization: Queue not found";
                    }

                    var queueUrl = queueList.QueueUrls[0];
                    var tasks = new List<Task<string>>();

                    if (siteType == SiteType.Rev)
                    {
                        for (var i = 0; i < revCustomers.Count; i++)
                        {
                            var customer = revCustomers[i];
                            var delaySeconds = Math.Min(i * 2, 900);
                            var isLastInstance = false;
                            if (i == revCustomers.Count - 1)
                                isLastInstance = true;
                            tasks.Add(EnqueueCrossProviderCustomerOptimizationSqsAsync(client, queueUrl, tenantId, customer.RevCustomerId, customer.RevIntegrationAuthId, serviceProviderIds, billPeriod.BillYear, billPeriod.BillMonth, billPeriod.id, optimizationSessionId, customer.SiteId, isLastInstance, siteType));

                            var optCus = new OptimizationCustomerProcessing()
                            {
                                CustomerId = customer.RevCustomerId,
                                StartTime = DateTime.UtcNow,
                                IsProcessed = false,
                                CustomerName = $"{customer.RevCustomerName} ({customer.RevCustomerId})",
                                ServiceProviderId = null,
                                SessionId = optimizationSessionId
                            };
                            altaWrxDb.OptimizationCustomerProcessings.Add(optCus);
                            optimizationSessionRepository.AddOptimizationInstanceToBeProcessed(optimizationSessionId, customer.AmopCustomerId.ToString(), null);
                        }
                        altaWrxDb.SaveChanges();
                    }

                    if (siteType == SiteType.AMOP)
                    {
                        for (var i = 0; i < amopCustomers.Count; i++)
                        {
                            var amopCustomer = amopCustomers[i];
                            var delaySeconds = Math.Min(i * 2, 900);
                            var isLastInstance = false;
                            if (i == amopCustomers.Count - 1)
                                isLastInstance = true;
                            tasks.Add(EnqueueCrossProviderCustomerOptimizationSqsAsync(client, queueUrl, tenantId, null, null, serviceProviderIds, billPeriod.BillYear, billPeriod.BillMonth, billPeriod.id, optimizationSessionId, amopCustomer.SiteId, isLastInstance, siteType));

                            var optCus = new OptimizationCustomerProcessing()
                            {
                                AMOPCustomerId = amopCustomer.SiteId,
                                AMOPCustomerName = amopCustomer.SiteName,
                                StartTime = DateTime.UtcNow,
                                IsProcessed = false,
                                ServiceProviderId = null,
                                SessionId = optimizationSessionId
                            };
                            altaWrxDb.OptimizationCustomerProcessings.Add(optCus);
                            optimizationSessionRepository.AddOptimizationInstanceToBeProcessed(optimizationSessionId, null, amopCustomer.SiteId);
                        }
                        altaWrxDb.SaveChanges();
                    }

                    var results = await Task.WhenAll(tasks);
                    var errorMessage = string.Join(Environment.NewLine, results.Where(result => !string.IsNullOrWhiteSpace(result)));
                    SendAllCustomersCrossProviderOptimizationSummaryEmail(serviceProviderIds, billingPeriodEnd, revCustomers, errorMessage);
                    return errorMessage;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error Queuing Customer Optimization for All Customers", ex);
                return "Error Queuing Customer Optimization: Exception occured";
            }


        }

        private IList<IOptimizationCustomersGetResult> GetCrossCustomerOptimization(CustomerBillingPeriod billPeriod, int tenantId, string serviceProviderIds, List<DateTime> endDateList, DateTime billingPeriodStart, DateTime billingPeriodEnd)
        {
            var allCustomer = GetOptimizationCustomers(tenantId, null, billPeriod.id, PortalTypes.CrossProvider, billingPeriodStart, billingPeriodEnd, serviceProviderIds);
            var siteIdList = altaWrxDb.Sites.Include(s => s.RevCustomer)
                    .Where(x => x.RevCustomer.id != null
                        && x.TenantId == tenantId
                        && x.RevCustomer.IsActive
                        && !x.RevCustomer.IsDeleted
                        && x.IsActive
                        && !x.IsDeleted
                        && endDateList.Any(date => date.Day == x.CustomerBillPeriodEndDay))
                    .Select(x => x.id)
                    .ToList();
            return allCustomer.Where(x => siteIdList.Any(siteId => siteId == x.SiteId)).ToList();
        }

        private IList<IOptimizationCustomersGetResult> GetOptimizationCustomers(int? tenantId, int? serviceProviderId, int? billPeriodId, PortalTypes portalType, DateTime? customerStartDate, DateTime? customerEndDate, string crossServiceProviderIds)
        {
            switch (portalType)
            {
                case PortalTypes.M2M:
                    return altaWrxDb.usp_OptimizationCustomersGet(tenantId, serviceProviderId, billPeriodId).Cast<IOptimizationCustomersGetResult>().ToList();
                case PortalTypes.Mobility:
                    return altaWrxDb.usp_Optimization_Mobility_CustomersGet(tenantId, serviceProviderId, billPeriodId).Cast<IOptimizationCustomersGetResult>().ToList();
                case PortalTypes.CrossProvider:
                    return altaWrxDb.usp_CrossProviderOptimizationCustomersGet(tenantId, crossServiceProviderIds, billPeriodId, customerStartDate, customerEndDate).Cast<IOptimizationCustomersGetResult>().ToList();
                default:
                    return new List<IOptimizationCustomersGetResult>();
            }
        }

        private IList<IOptimizationAMOPCustomersGetResult> GetOptimizationAMOPCustomers(int? tenantId, int? serviceProviderId, int? billPeriodId, PortalTypes portalType)
        {
            switch (portalType)
            {
                case PortalTypes.M2M:
                    return altaWrxDb.usp_Optimization_AMOPCustomersGet(tenantId, serviceProviderId, billPeriodId).Cast<IOptimizationAMOPCustomersGetResult>().ToList();
                case PortalTypes.Mobility:
                    return altaWrxDb.usp_Optimization_Mobility_AMOPCustomersGet(tenantId, serviceProviderId, billPeriodId).Cast<IOptimizationAMOPCustomersGetResult>().ToList();
                default:
                    return new List<IOptimizationAMOPCustomersGetResult>();
            }
        }

        private void SendAllCustomersOptimizationSummaryEmail(int serviceProviderId, DateTime billingPeriodEnd, IEnumerable<IOptimizationCustomersGetResult> customers, string errorMessage)
        {
            var settings = altaWrxDb.OptimizationSettings.Where(setting => !setting.IsDeleted).ToList();
            var toEmails = settings.FirstOrDefault(setting => setting.SettingKey == "CustomerOptimizationToEmailAddresses")
                ?.SettingValue?.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var fromEmail = settings.FirstOrDefault(setting => setting.SettingKey == "CustomerOptimizationFromEmailAddress")?.SettingValue;
            if (toEmails == null || toEmails.Length < 1 || fromEmail == null)
            {
                Log.Error("Error sending 'All Customers' summary email - missing configuration");
                return;
            }

            var serviceProvider = altaWrxDb.ServiceProviders.Find(serviceProviderId);
            var serviceProviderName = serviceProvider?.DisplayName ?? string.Empty;

            var emailClient = new SESWrapper
            {
                From = fromEmail,
                Subject = BuildAllCustomersSummaryEmailSubject(serviceProviderName, billingPeriodEnd),
                Body = BuildAllCustomersSummaryEmailBody(serviceProviderName, billingPeriodEnd, customers, errorMessage)
            };
            foreach (var toEmail in toEmails)
            {
                emailClient.AddRecipient(toEmail);
            }

            if (user != null)
            {
                emailClient.AddCCRecipient(user.Email);
            }

            emailClient.SendEmail();
        }

        private void SendAllCustomersCrossProviderOptimizationSummaryEmail(string serviceProviderIds, DateTime billingPeriodEnd, IEnumerable<IOptimizationCustomersGetResult> customers, string errorMessage)
        {
            var settings = altaWrxDb.OptimizationSettings.Where(setting => !setting.IsDeleted).ToList();
            var toEmails = settings.FirstOrDefault(setting => setting.SettingKey == "CustomerOptimizationToEmailAddresses")
                ?.SettingValue?.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var fromEmail = settings.FirstOrDefault(setting => setting.SettingKey == "CustomerOptimizationFromEmailAddress")?.SettingValue;
            if (toEmails == null || toEmails.Length < 1 || fromEmail == null)
            {
                Log.Error("Error sending 'All Customers' summary email - missing configuration");
                return;
            }

            var serviceProviderIdList = serviceProviderIds.Split(',');

            var serviceProviderList = altaWrxDb.ServiceProviders.Where(x => serviceProviderIdList.Any(s => s == x.id.ToString()) && x.DisplayName != null).Select(x => x.DisplayName).ToList();
            var serviceProviderNameList = string.Join(",", serviceProviderList);

            var emailClient = new SESWrapper
            {
                From = fromEmail,
                Subject = BuildAllCustomersSummaryEmailSubject(serviceProviderNameList, billingPeriodEnd),
                Body = BuildAllCustomersSummaryEmailBody(serviceProviderNameList, billingPeriodEnd, customers, errorMessage)
            };
            foreach (var toEmail in toEmails)
            {
                emailClient.AddRecipient(toEmail);
            }

            if (user != null)
            {
                emailClient.AddCCRecipient(user.Email);
            }

            emailClient.SendEmail();
        }

        private static string BuildAllCustomersSummaryEmailSubject(string serviceProviderName, DateTime billingPeriodEnd)
        {
            var env = ConfigurationManager.AppSettings["Published_Environment"];
            var subject = $"{serviceProviderName} Optimization Summary - All Customers";
            return env == "Production" ? subject : subject + $" ({env})";
        }

        private static string BuildAllCustomersSummaryEmailBody(string serviceProviderName, DateTime billingPeriodEnd, IEnumerable<IOptimizationCustomersGetResult> customers, string errorMessage)
        {
            var stringBuilder = new StringBuilder($@"
                <html>
                <head>
                <style>
                body {{
                    background-color: #fff;
                    font-family: ""Lato"", ""Helvetica Neue"", Helvetica, Arial, sans-serif;
                    z-index: 0;
                    position: relative;
                    top: 0;
                    left: 0;
                    top: 0;
                    bottom: 0;
                }}

                tr {{
                    text-align: left;
                }}

                th,td {{
                    padding-right: 10px;
                }}

                </style>
                </head>
                <h1>{serviceProviderName} Optimization Summary - All Customers</h1>
                <h2>For billing period ending {billingPeriodEnd:g}</h2>
                <table>
                <tr><th>Service Provider</th><th>Billing Period End</th><th>Customer Name</th><th>Rev Account #</th><th>Eligible Device Count</th></tr>");

            foreach (var customer in customers)
            {
                stringBuilder.Append(
                    $"<tr><td>{serviceProviderName}</td><td>{billingPeriodEnd:g}</td><td>{customer.RevCustomerName}</td><td>{customer.RevCustomerId}</td><td>{customer.SimCount ?? 0}</td></tr>");
            }

            stringBuilder.Append("</table>");

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                stringBuilder.Append($"<h2>Errors</h2><p>{errorMessage.Replace(Environment.NewLine, "<br>")}</p>");
            }

            stringBuilder.Append("</html>");
            return stringBuilder.ToString();
        }


        private async Task<string> EnqueueCarrierOptimizationAsync(BillingPeriod billPeriod, int? serviceProviderId,
            IList<CustomObject> customObjectDbList, string awsAccessKey, string awsSecretAccessKey, long optimizationSessionId)
        {
            if (!serviceProviderId.HasValue)
            {
                return "Service Provider must be supplied to start carrier optimization.";
            }

            if (!permissionManager.UserIsSuperAdmin(Session) && !permissionManager.UserIsTenantAdmin(Session))
            {
                return "Insufficient privileges to queue carrier optimization.";
            }

            string carrierOptimizationQueueName = CarrierOptimizationQueueFromCustomObjects(customObjectDbList);

            return await EnqueueCarrierOptimizationSqsAsync(billPeriod, permissionManager.Tenant.id, awsAccessKey,
                awsSecretAccessKey, carrierOptimizationQueueName, serviceProviderId.Value, optimizationSessionId);
        }

        public ActionResult DownloadResults(long id)
        {
            Zipper z = new Zipper();
            string downloadFileName = $"OptimizationResults{DateTime.Now.ToString("yyyyMMdd_hhmmss")}.zip";
            var results = altaWrxDb.OptimizationInstanceResultFiles.Find(id);
            if (results == null)
            {
                return HttpNotFound();
            }
            else
            {
                z.AddFile(new MemoryStream(buffer: results.AssignmentXlsxBytes), string.Empty, "device_assignments.xlsx");
                byte[] data = z.GetStream();
                return File(data, "application/octet-stream", downloadFileName);
            }
        }

        private string CarrierOptimizationQueueFromCustomObjects(IList<CustomObject> customObjectDbList)
        {
            return ValueFromCustomObjects(customObjectDbList, CARRIER_OPTIMIZATION_QUEUE_NAME);
        }

        private string M2MCustomerOptimizationQueueFromCustomObjects(IList<CustomObject> customObjectDbList)
        {
            return ValueFromCustomObjects(customObjectDbList, M2M_CUSTOMER_OPTIMIZATION_QUEUE_NAME);
        }

        private string MobilityCustomerOptimizationQueueFromCustomObjects(IList<CustomObject> customObjectDbList)
        {
            return ValueFromCustomObjects(customObjectDbList, MOBILITY_CUSTOMER_OPTIMIZATION_QUEUE_NAME);
        }

        private string CrossProviderCustomerOptimizationQueueFromCustomObjects(IList<CustomObject> customObjectDbList)
        {
            return ValueFromCustomObjects(customObjectDbList, CROSS_PROVIDER_CUSTOMER_OPTIMIZATION_QUEUE_NAME);
        }

        private string RatePlanQueueFromCustomObjects(IList<CustomObject> customObjectDbList)
        {
            return ValueFromCustomObjects(customObjectDbList, RATE_PLAN_QUEUE_NAME);
        }

        private async Task<string> EnqueueRatePlanSqsAsync(long instanceId, string awsAccessKey, string awsSecretAccessKey, string ratePlanQueueName, ServiceProvider serviceProvider)
        {
            try
            {
                var awsCredentials = new Amazon.Runtime.BasicAWSCredentials(awsAccessKey, awsSecretAccessKey);
                using (var client = new AmazonSQSClient(awsCredentials, Amazon.RegionEndpoint.USEast1))
                {
                    var queueList = client.ListQueues(ratePlanQueueName);
                    if (queueList.HttpStatusCode == System.Net.HttpStatusCode.OK && queueList.QueueUrls != null && queueList.QueueUrls.Count > 0)
                    {
                        var requestMsgBody = $"Rate Plan Update for Instance {instanceId}";
                        var messageAttributes = new Dictionary<string, MessageAttributeValue>
                        {
                            {
                                "InstanceId", new MessageAttributeValue
                                { DataType = "String", StringValue = instanceId.ToString()}
                            }
                        };
                        if (serviceProvider.IntegrationId == (int)IntegrationEnum.Telegence)
                        {
                            messageAttributes.Add("SyncedDevices", new MessageAttributeValue
                            {
                                DataType = "String",
                                StringValue = true.ToString()
                            });
                        }
                        var request = new SendMessageRequest
                        {
                            MessageAttributes = messageAttributes,
                            MessageBody = requestMsgBody,
                            QueueUrl = queueList.QueueUrls[0]
                        };

                        var response = await client.SendMessageAsync(request);
                        return response.HttpStatusCode.IsSuccessStatusCode()
                            ? string.Empty
                            : $"Error Queuing Rate Plan Changes: {response.HttpStatusCode:D} {response.HttpStatusCode:G}";
                    }

                    return "Error Queuing Rate Plan Changes: Queue not found";
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error Queuing Optimization", ex);
                return "Error Queue Optimization: Exception occured";
            }
        }

        private async Task<string> EnqueueCarrierOptimizationSqsAsync(BillingPeriod billPeriod, int tenantId, string awsAccessKey,
            string awsSecretAccessKey, string carrierOptimizationQueueName, int serviceProviderId, long optimizationSessionId)
        {
            try
            {
                var awsCredentials = new Amazon.Runtime.BasicAWSCredentials(awsAccessKey, awsSecretAccessKey);
                using (var client = new AmazonSQSClient(awsCredentials, Amazon.RegionEndpoint.USEast1))
                {
                    var queueList = client.ListQueues(carrierOptimizationQueueName);
                    if (queueList.HttpStatusCode == System.Net.HttpStatusCode.OK && queueList.QueueUrls != null && queueList.QueueUrls.Count > 0)
                    {
                        var requestMsgBody = $"Carrier to optimize is for Billing Period {billPeriod.BillYear}/{billPeriod.BillMonth}";
                        var request = new SendMessageRequest
                        {
                            MessageAttributes = new Dictionary<string, MessageAttributeValue>
                            {
                                {"ServiceProviderId", new MessageAttributeValue {DataType = "String", StringValue = serviceProviderId.ToString()}},
                                {"TenantId", new MessageAttributeValue {DataType = "String", StringValue = tenantId.ToString()}},
                                {"BillPeriodId", new MessageAttributeValue {DataType = "String", StringValue = billPeriod.id.ToString()}},
                                {"OptimizationSessionId", new MessageAttributeValue {DataType = "String", StringValue = optimizationSessionId.ToString()}},
                                {SQSMessageKeyConstant.PORTAL_TYPE_ID, new MessageAttributeValue {DataType = nameof(String), StringValue = billPeriod.ServiceProvider.Integration.PortalTypeId.ToString()}}
                            },
                            MessageBody = requestMsgBody,
                            QueueUrl = queueList.QueueUrls[0]
                        };

                        // Skip sync if billing period is closed or carrier does not support sync on optimization
                        if (billPeriod.BillingCycleEndDate < DateTime.Now
                            || billPeriod.ServiceProvider.IntegrationId == (int)IntegrationEnum.Telegence)
                        {
                            request.MessageAttributes.Add("HasSynced", new MessageAttributeValue { DataType = "String", StringValue = true.ToString() });
                        }

                        var response = await client.SendMessageAsync(request);
                        return response.HttpStatusCode.IsSuccessStatusCode()
                            ? string.Empty
                            : $"Error Queuing Optimization: {response.HttpStatusCode:D} {response.HttpStatusCode:G}";
                    }

                    return "Error Queuing Optimization: Queue not found";
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error Queuing Optimization", ex);
                return "Error Queue Optimization: Exception occured";
            }
        }

        private async Task<string> EnqueueCustomerOptimizationSqsAsync(BillingPeriod billPeriod, CustomerBillingPeriod customerBillPeriod, int tenantId, int siteId,
            int? integrationAuthenticationId, int? serviceProviderId, string serviceProviderIds, string awsAccessKey, string awsSecretAccessKey,
            string m2mCustomerOptimizationQueueName, string mobilityCustomerOptimizationQueueName, string crossProviderCustomerOptimizationQueueName, string revCustId, long optimizationSessionId, SiteType siteType)
        {
            try
            {
                var m2mServiceProviders = altaWrxDb.usp_OptimizationServiceProvidersByCustomer(siteId.ToString(), (int)PortalTypes.M2M)?.ToList();
                var mobilityServiceProviders = altaWrxDb.usp_OptimizationServiceProvidersByCustomer(siteId.ToString(), (int)PortalTypes.Mobility)?.ToList();

                string result = string.Empty;
                if (m2mServiceProviders != null && m2mServiceProviders.Any() && (serviceProviderId == null || m2mServiceProviders.Contains(serviceProviderId.Value)) && !permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization)
                {
                    result = await EnqueueCustomerOptimizationSqsAsync(billPeriod, tenantId, revCustId, integrationAuthenticationId,
                        serviceProviderId, awsAccessKey, awsSecretAccessKey, m2mCustomerOptimizationQueueName, optimizationSessionId, siteId, siteType);
                }

                if (mobilityServiceProviders != null && mobilityServiceProviders.Any() && (serviceProviderId == null || mobilityServiceProviders.Contains(serviceProviderId.Value)) && !permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization)
                {
                    mobilityServiceProviders = serviceProviderId != null ? new List<int?> { serviceProviderId } : mobilityServiceProviders;
                    result = await EnqueueCustomerOptimizationSqsAsync(billPeriod, tenantId, revCustId, integrationAuthenticationId,
                        mobilityServiceProviders, awsAccessKey, awsSecretAccessKey, mobilityCustomerOptimizationQueueName, optimizationSessionId, siteId, siteType);
                }

                if (!string.IsNullOrWhiteSpace(serviceProviderIds) && permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization)
                {
                    result = await EnqueueCrossProviderCustomerOptimizationSqsAsync(customerBillPeriod, tenantId, revCustId, integrationAuthenticationId,
                        serviceProviderIds, awsAccessKey, awsSecretAccessKey, crossProviderCustomerOptimizationQueueName, optimizationSessionId, siteId, siteType);
                }

                return result;
            }
            catch (Exception ex)
            {
                Log.Error($"Error Queuing Optimization for {siteId}", ex);
                return "Error Queuing Optimization: Exception occured";
            }
        }

        private async Task<string> EnqueueCustomerOptimizationSqsAsync(BillingPeriod billPeriod, int tenantId, string revCustId,
            int? integrationAuthenticationId, List<int?> serviceProviders, string awsAccessKey, string awsSecretAccessKey,
            string customerOptimizationQueueName, long optimizationSessionId, int? AMOPCustomerId, SiteType siteType)
        {
            StringBuilder sb = new StringBuilder();
            foreach (var providerId in serviceProviders)
            {
                var result = await EnqueueCustomerOptimizationSqsAsync(billPeriod, tenantId, revCustId, integrationAuthenticationId, providerId,
                    awsAccessKey, awsSecretAccessKey, customerOptimizationQueueName, optimizationSessionId, AMOPCustomerId, siteType);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    sb.AppendLine(result);
                }
            }

            return sb.ToString();
        }

        private async Task<string> EnqueueCustomerOptimizationSqsAsync(BillingPeriod billPeriod, int tenantId, string revCustId,
            int? integrationAuthenticationId, int? serviceProviderId, string awsAccessKey, string awsSecretAccessKey,
            string customerOptimizationQueueName, long optimizationSessionId, int? AMOPCustomerId, SiteType siteType)
        {
            try
            {
                var awsCredentials = new Amazon.Runtime.BasicAWSCredentials(awsAccessKey, awsSecretAccessKey);
                using (var client = new AmazonSQSClient(awsCredentials, Amazon.RegionEndpoint.USEast1))
                {
                    var queueList = client.ListQueues(customerOptimizationQueueName);
                    if (queueList.HttpStatusCode != System.Net.HttpStatusCode.OK || queueList.QueueUrls == null || queueList.QueueUrls.Count <= 0)
                    {
                        return "Error Queuing Customer Optimization: Queue not found";
                    }

                    var queueUrl = queueList.QueueUrls[0];
                    return await EnqueueCustomerOptimizationSqsAsync(client, queueUrl, tenantId, revCustId, integrationAuthenticationId,
                        serviceProviderId, billPeriod.BillYear, billPeriod.BillMonth, billPeriod.id, optimizationSessionId, AMOPCustomerId, true, siteType);

                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error Queuing Customer Optimization for {revCustId}", ex);
                return "Error Queuing Customer Optimization: Exception occured";
            }
        }

        private static async Task<string> EnqueueCustomerOptimizationSqsAsync(IAmazonSQS sqsClient, string queueUrl, int tenantId, string revCustId,
            int? integrationAuthenticationId, int? serviceProviderId, int billYear, int billMonth, int billPeriodId, long optimizationSessionId, int? AMOPCustomerId, bool isLastInstance,
            SiteType siteType = SiteType.Rev, int delaySeconds = 0)
        {
            var requestMsgBody = $"Customer to optimize is {revCustId} for Billing Period {billYear}/{billMonth}";

            var messageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                { "TenantId", new MessageAttributeValue { DataType = "String", StringValue = tenantId.ToString() } },
                { "BillPeriodId", new MessageAttributeValue { DataType = "String", StringValue = billPeriodId.ToString() } },
                { "OptimizationSessionId", new MessageAttributeValue { DataType = "String", StringValue = optimizationSessionId.ToString() } },
                { "CustomerType", new MessageAttributeValue { DataType = "String", StringValue = ((int)siteType).ToString() } },
                { "IsLastInstance", new MessageAttributeValue { DataType = "String", StringValue = isLastInstance.ToString() } }
        };
            if (siteType == SiteType.AMOP && AMOPCustomerId.HasValue)
            {
                messageAttributes.Add("AMOPCustomerId", new MessageAttributeValue { DataType = "String", StringValue = AMOPCustomerId.ToString() });
            }

            if (siteType == SiteType.Rev)
            {
                messageAttributes.Add("IntegrationAuthenticationId", new MessageAttributeValue { DataType = "String", StringValue = integrationAuthenticationId.ToString() });
                messageAttributes.Add("CustomerId", new MessageAttributeValue { DataType = "String", StringValue = revCustId });
            }
            // include service provider id, if specified
            if (serviceProviderId != null)
            {
                messageAttributes.Add("ServiceProviderId",
                    new MessageAttributeValue { DataType = "String", StringValue = serviceProviderId.Value.ToString() });
            }

            var request = new SendMessageRequest
            {
                MessageAttributes = messageAttributes,
                MessageBody = requestMsgBody,
                QueueUrl = queueUrl,
                DelaySeconds = delaySeconds
            };

            var response = await sqsClient.SendMessageAsync(request);
            return response.HttpStatusCode.IsSuccessStatusCode()
                ? string.Empty
                : $"Error Queuing Customer Optimization: {response.HttpStatusCode:D} {response.HttpStatusCode:G}";
        }

        private async Task<string> EnqueueCrossProviderCustomerOptimizationSqsAsync(CustomerBillingPeriod customerBillingPeriod, int tenantId, string revCustId,
            int? integrationAuthenticationId, string serviceProviderIds, string awsAccessKey, string awsSecretAccessKey,
            string customerOptimizationQueueName, long optimizationSessionId, int? AMOPCustomerId, SiteType siteType)
        {
            try
            {
                var awsCredentials = new Amazon.Runtime.BasicAWSCredentials(awsAccessKey, awsSecretAccessKey);
                using (var client = new AmazonSQSClient(awsCredentials, Amazon.RegionEndpoint.USEast1))
                {
                    var queueList = client.ListQueues(customerOptimizationQueueName);
                    if (queueList.HttpStatusCode != System.Net.HttpStatusCode.OK || queueList.QueueUrls == null || queueList.QueueUrls.Count <= 0)
                    {
                        return LogCommonStrings.ERROR_QUEUING_CUSTOMER_OPTIMIZATION_QUEUE_NOT_FOUND;
                    }

                    var queueUrl = queueList.QueueUrls[0];
                    return await EnqueueCrossProviderCustomerOptimizationSqsAsync(client, queueUrl, tenantId, revCustId, integrationAuthenticationId,
                        serviceProviderIds, customerBillingPeriod.BillYear, customerBillingPeriod.BillMonth, customerBillingPeriod.id, optimizationSessionId, AMOPCustomerId, true, siteType);

                }
            }
            catch (Exception ex)
            {
                Log.Error(string.Format(LogCommonStrings.ERROR_QUEUING_CUSTOMER_OPTIMIZATION_FOR_CUSTOMER, revCustId), ex);
                return LogCommonStrings.ERROR_QUEUING_CUSTOMER_OPTIMIZATION_EXCEPTION_OCCURED;
            }
        }

        private async Task<string> EnqueueCrossProviderCustomerOptimizationSqsAsync(IAmazonSQS sqsClient, string queueUrl, int tenantId, string revCustId,
           int? integrationAuthenticationId, string serviceProviderIds, int billYear, int billMonth, int billPeriodId, long optimizationSessionId, int? AMOPCustomerId, bool isLastInstance,
           SiteType siteType = SiteType.Rev, int delaySeconds = 0)
        {
            var requestMsgBody = $"Customer to optimize is {revCustId} for Billing Period {billYear}/{billMonth}";

            var messageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                { SQSMessageKeyConstant.TENANT_ID, new MessageAttributeValue { DataType = nameof(String), StringValue = tenantId.ToString() } },
                { SQSMessageKeyConstant.OPTIMIZATION_SESSION_ID, new MessageAttributeValue { DataType = nameof(String), StringValue = optimizationSessionId.ToString() } },
                { SQSMessageKeyConstant.CUSTOMER_TYPE, new MessageAttributeValue { DataType = nameof(String), StringValue = ((int)siteType).ToString() } },
                { SQSMessageKeyConstant.IS_LAST_INSTANCE, new MessageAttributeValue { DataType = nameof(String), StringValue = isLastInstance.ToString() } },
                { SQSMessageKeyConstant.PORTAL_TYPE_ID, new MessageAttributeValue { DataType = nameof(String), StringValue = ((int)PortalTypes.CrossProvider).ToString() } },
            };

            if (permissionManager.OptimizationSettings.OptIntoCrossProviderCustomerOptimization)
            {
                messageAttributes.Add(SQSMessageKeyConstant.SERVICE_PROVIDER_IDS, new MessageAttributeValue { DataType = nameof(String), StringValue = serviceProviderIds.ToString() });
                messageAttributes.Add(SQSMessageKeyConstant.AMOP_CUSTOMER_ID, new MessageAttributeValue { DataType = nameof(String), StringValue = AMOPCustomerId.ToString() });
                messageAttributes.Add(SQSMessageKeyConstant.CUSTOMER_BILLING_PERIOD_ID, new MessageAttributeValue { DataType = nameof(String), StringValue = billPeriodId.ToString() });
            }
            else
            {
                messageAttributes.Add(SQSMessageKeyConstant.BILL_PERIOD_ID, new MessageAttributeValue { DataType = nameof(String), StringValue = billPeriodId.ToString() });
                if (siteType == SiteType.Rev)
                {
                    messageAttributes.Add(SQSMessageKeyConstant.INTEGRATION_AUTHENTICATION_ID, new MessageAttributeValue { DataType = nameof(String), StringValue = integrationAuthenticationId.ToString() });
                    messageAttributes.Add(SQSMessageKeyConstant.CUSTOMER_ID, new MessageAttributeValue { DataType = nameof(String), StringValue = revCustId });
                }
            }

            var request = new SendMessageRequest
            {
                MessageAttributes = messageAttributes,
                MessageBody = requestMsgBody,
                QueueUrl = queueUrl,
                DelaySeconds = delaySeconds
            };

            var response = await sqsClient.SendMessageAsync(request);
            return response.HttpStatusCode.IsSuccessStatusCode()
                ? string.Empty
                : $"{LogCommonStrings.ERROR_QUEUING_CUSTOMER_OPTIMIZATION}: {response.HttpStatusCode:D} {response.HttpStatusCode:G}";
        }

        public ActionResult OptimizationMultiSessionListDropdown(string selectedSession, string controlId = "OptimizationSessionId")
        {
            ViewData["ControlId"] = controlId;
            var model = ListHelper.GetOptimizationSessionFilterList(permissionManager, selectedSession);
            return PartialView("OptimizationMultiSessionListDropdown", model);
        }

        private bool CheckExistRatePlanAdjustmentInBillingPeriod(long instanceId, ServiceProvider serviceProvider)
        {
            var optimizationInstance = altaWrxDb.OptimizationInstances.FirstOrDefault(x => x.Id == instanceId);
            if (serviceProvider.IntegrationId == (int)IntegrationEnum.Telegence)
            {
                var optimizationRatePlanUpdateSummaries = altaWrxDb.OptimizationRatePlanUpdateSummaries.Include(x => x.OptimizationInstance)
                    .Any(x => x.OptimizationInstance.ServiceProviderId == optimizationInstance.ServiceProvider.id &&
                                x.OptimizationInstance.BillingPeriodStartDate == optimizationInstance.BillingPeriodStartDate &&
                                x.OptimizationInstance.BillingPeriodEndDate == optimizationInstance.BillingPeriodEndDate);
                return optimizationRatePlanUpdateSummaries;
            }
            else
            {
                return false;
            }

        }
    }
}
