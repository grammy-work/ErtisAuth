using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ertis.MongoDB.Queries;
using ErtisAuth.Hub.Constants;
using ErtisAuth.Core.Models.Memberships;
using ErtisAuth.Core.Models.Roles;
using ErtisAuth.Extensions.Authorization.Annotations;
using ErtisAuth.Identity.Attributes;
using ErtisAuth.Sdk.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using ErtisAuth.Hub.Extensions;
using ErtisAuth.Hub.ViewModels;
using ErtisAuth.Hub.ViewModels.Memberships;
using Ertis.Security.Cryptography;

namespace ErtisAuth.Hub.Controllers
{
    [Authorized]
	[RbacResource("memberships")]
	[Route("memberships")]
	public class MembershipsController : Controller
	{
		#region Constants

		private static readonly string DefaultEncoding = Core.Constants.Defaults.DEFAULT_ENCODING.HeaderName;
		private static readonly string DefaultHashAlgorithm = Core.Constants.Defaults.DEFAULT_HASH_ALGORITHM.ToString().Replace('_', '-');

		#endregion
		
		#region Services

		private readonly IMembershipService membershipService;
		private readonly IMailHookService mailHookService;
		private readonly IAuthenticationService authenticationService;

		#endregion

		#region Constructors

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="membershipService"></param>
		/// <param name="mailHookService"></param>
		/// <param name="authenticationService"></param>
		public MembershipsController(IMembershipService membershipService, IMailHookService mailHookService, IAuthenticationService authenticationService)
		{
			this.membershipService = membershipService;
			this.mailHookService = mailHookService;
			this.authenticationService = authenticationService;
		}

		#endregion
		
		#region Index

		[HttpGet]
		public IActionResult Index()
		{
			var viewModel = new MembershipsViewModel
			{
				CreateViewModel = this.GetMembershipCreateViewModel()
			};
			
			var routedModel = this.GetRedirectionParameter<SerializableViewModel>();
			if (routedModel != null)
			{
				viewModel.IsSuccess = routedModel.IsSuccess;
				viewModel.ErrorMessage = routedModel.ErrorMessage;
				viewModel.SuccessMessage = routedModel.SuccessMessage;
				viewModel.Error = routedModel.Error;
				viewModel.Errors = routedModel.Errors;
			}
			
			return View(viewModel);
		}

		#endregion
		
		#region Create

		[HttpPost("create")]
		[RbacAction(Rbac.CrudActions.Create)]
		public async Task<IActionResult> Create([FromForm] MembershipCreateViewModel model)
		{
			if (this.ModelState.IsValid)
			{
				var membership = new Membership
				{
					Name = model.Name
				};

				var createMembershipResponse = await this.membershipService.CreateMembershipAsync(membership, this.GetBearerToken());
				if (createMembershipResponse.IsSuccess)
				{
					model.IsSuccess = true;
					model.SuccessMessage = "Membership created";
					this.SetRedirectionParameter(new SerializableViewModel(model));
					return this.RedirectToAction("Detail", routeValues: new { id = createMembershipResponse.Data.Id });
				}
				else
				{
					model.SetError(createMembershipResponse);
					this.SetRedirectionParameter(new SerializableViewModel(model));
					return this.RedirectToAction("Index");
				}
			}
			else
			{
				model.IsSuccess = false;
				model.Errors = this.ModelState.Values.SelectMany(x => x.Errors.Select(y => y.ErrorMessage));
			}
			
			this.SetRedirectionParameter(new SerializableViewModel(model));
			return this.RedirectToAction("Index");
		}
		
		private MembershipCreateViewModel GetMembershipCreateViewModel(MembershipCreateViewModel currentModel = null)
		{
			var encodings = Encoding.GetEncodings();
			var hashAlgorithms = Enum.GetNames<HashAlgorithms>().Select(x => x.Replace('_', '-'));
			
			var model = currentModel ?? new MembershipCreateViewModel
			{
				ExpiresIn = 43200,
				RefreshTokenExpiresIn = 86400,
				DefaultEncoding = DefaultEncoding,
				DefaultLanguage = TextSearchLanguage.None.ISO6391Code,
				HashAlgorithm = DefaultHashAlgorithm,
				HashAlgorithmList = hashAlgorithms.Select(x => new SelectListItem(x, x)).ToList(),
				EncodingList = encodings.Select(x => new SelectListItem(x.DisplayName, x.Name)).ToList(),
				LanguageList = TextSearchLanguage.All.Select(x => new SelectListItem(x.Name, x.ISO6391Code)).ToList().ToList()
			};
			
			return model;
		}

		#endregion
		
		#region Detail

		[HttpGet("{id}")]
		[RbacObject("{id}")]
		public async Task<IActionResult> Detail(string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return this.RedirectToAction("Index");
			}

			var viewModel = await this.GetDetailViewModelAsync(id);
			return View(viewModel);
		}

		private async Task<MembershipViewModel> GetDetailViewModelAsync(string id)
		{
			var token = this.GetBearerToken();
			var getMembershipResponse = await this.membershipService.GetMembershipAsync(id, token);
			if (getMembershipResponse.IsSuccess)
			{
				var encodings = Encoding.GetEncodings();
				var hashAlgorithms = Enum.GetNames<HashAlgorithms>().Select(x => x.Replace('_', '-'));
				
				var viewModel = new MembershipViewModel
				{
					Id = getMembershipResponse.Data.Id,
					Name = getMembershipResponse.Data.Name,
					ExpiresIn = getMembershipResponse.Data.ExpiresIn,
					RefreshTokenExpiresIn = getMembershipResponse.Data.RefreshTokenExpiresIn,
					SecretKey = getMembershipResponse.Data.SecretKey,
					HashAlgorithm = getMembershipResponse.Data.HashAlgorithm,
					DefaultEncoding = getMembershipResponse.Data.DefaultEncoding,
					DefaultLanguage = getMembershipResponse.Data.DefaultLanguage,
					HashAlgorithmList = hashAlgorithms.Select(x => new SelectListItem(x, x)).ToList(),
					EncodingList = encodings.Select(x => new SelectListItem(x.DisplayName, x.Name)).ToList(),
					LanguageList = TextSearchLanguage.All.Select(x => new SelectListItem(x.Name, x.ISO6391Code)).ToList().ToList(),
					MailSettings = getMembershipResponse.Data.MailSettings,
					Sys = getMembershipResponse.Data.Sys,
				};

				var routedModel = this.GetRedirectionParameter<SerializableViewModel>();
				if (routedModel != null)
				{
					viewModel.IsSuccess = routedModel.IsSuccess;
					viewModel.ErrorMessage = routedModel.ErrorMessage;
					viewModel.SuccessMessage = routedModel.SuccessMessage;
					viewModel.Error = routedModel.Error;
					viewModel.Errors = routedModel.Errors;
				}
					
				return viewModel;
			}
			else
			{
				var viewModel = new MembershipViewModel();
				viewModel.SetError(getMembershipResponse);
				return viewModel;
			}
		}
		
		#endregion

		#region Update

		[HttpPost]
		[RbacAction(Rbac.CrudActions.Update)]
		public async Task<IActionResult> Update([FromForm] MembershipViewModel model)
		{
			if (this.ModelState.IsValid)
			{
				var token = this.GetBearerToken();
				Membership currentMembership = null;
				var getMembershipResponse = await this.membershipService.GetMembershipAsync(model.Id, token);
				if (getMembershipResponse.IsSuccess)
				{
					currentMembership = getMembershipResponse.Data;
				}
				
				var membership = new Membership
				{
					Id = model.Id,
					Name = model.Name,
					ExpiresIn = model.ExpiresIn,
					RefreshTokenExpiresIn = model.RefreshTokenExpiresIn,
					SecretKey = model.SecretKey,
					HashAlgorithm = model.HashAlgorithm,
					DefaultEncoding = model.DefaultEncoding,
					DefaultLanguage = model.DefaultLanguage,
					MailSettings = model.MailSettings,
				};

				var updateMembershipResponse = await this.membershipService.UpdateMembershipAsync(membership, token);
				if (updateMembershipResponse.IsSuccess)
				{
					model.IsSuccess = true;
					model.SuccessMessage = "Membership updated";
				}
				else
				{
					model.SetError(updateMembershipResponse);
				}
			}
			else
			{
				model.IsSuccess = false;
				model.Errors = this.ModelState.Values.SelectMany(x => x.Errors.Select(y => y.ErrorMessage));
			}

			this.SetRedirectionParameter(new SerializableViewModel(model));
			return this.RedirectToAction("Detail", routeValues: new { id = model.Id });
		}

		#endregion
		
		#region Delete

		[HttpPost("delete")]
		[RbacAction(Rbac.CrudActions.Delete)]
		public async Task<IActionResult> Delete([FromForm]DeleteViewModel deleteMembershipModel)
		{
			if (this.ModelState.IsValid)
			{
				var username = this.GetClaim(Claims.Username);
				var getTokenResponse = await this.authenticationService.GetTokenAsync(username, deleteMembershipModel.Password);
				if (getTokenResponse.IsSuccess)
				{
					var deleteResponse = await this.membershipService.DeleteMembershipAsync(deleteMembershipModel.ItemId, this.GetBearerToken());
					if (deleteResponse.IsSuccess)
					{
						this.SetRedirectionParameter(new SerializableViewModel
						{
							IsSuccess = true,
							SuccessMessage = "Membership deleted"
						});
					}
					else
					{
						var model = new SerializableViewModel();
						model.SetError(deleteResponse);
						this.SetRedirectionParameter(model);
					}
				}
				else
				{
					var model = new SerializableViewModel();
					model.SetError(getTokenResponse);
					this.SetRedirectionParameter(model);
				}
			}
			
			return this.RedirectToAction("Index");
		}

		#endregion
		
		#region Email Settings

		[HttpGet("{id}/mail-settings")]
		public async Task<IActionResult> MailSettings(string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return this.RedirectToAction("Index");
			}
			
			var token = this.GetBearerToken();
			var viewModel = await this.GetDetailViewModelAsync(id);
			
			var getMailHooksResponse = await this.mailHookService.GetAsync(token);
			if (getMailHooksResponse.IsSuccess)
			{
				var mailHooks = getMailHooksResponse.Data.Items;
				viewModel.MailHooks = mailHooks;
			}
			
			return View(viewModel);
		}

		#endregion
	}
}