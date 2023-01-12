using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ertis.Core.Collections;
using Ertis.Core.Models.Resources;
using Ertis.MongoDB.Queries;
using Ertis.Schema.Dynamics;
using Ertis.Schema.Exceptions;
using Ertis.Schema.Extensions;
using Ertis.Schema.Types.CustomTypes;
using Ertis.Schema.Validation;
using Ertis.Security.Cryptography;
using ErtisAuth.Core.Models.Identity;
using ErtisAuth.Abstractions.Services.Interfaces;
using ErtisAuth.Core.Exceptions;
using ErtisAuth.Core.Helpers;
using ErtisAuth.Core.Models.Users;
using ErtisAuth.Core.Models.Events;
using ErtisAuth.Core.Models.Memberships;
using ErtisAuth.Dao.Repositories.Interfaces;
using ErtisAuth.Events.EventArgs;
using ErtisAuth.Identity.Jwt.Services.Interfaces;
using ErtisAuth.Integrations.OAuth.Core;

namespace ErtisAuth.Infrastructure.Services
{
    public class UserService : DynamicObjectCrudService, IUserService
    {
        #region Services
        
        private readonly IUserTypeService _userTypeService;
        private readonly IMembershipService _membershipService;
        private readonly IRoleService _roleService;
        private readonly IEventService _eventService;
        private readonly IJwtService _jwtService;
        private readonly ICryptographyService _cryptographyService;

        #endregion
        
        #region Constructors
        
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="userTypeService"></param>
        /// <param name="membershipService"></param>
        /// <param name="roleService"></param>
        /// <param name="eventService"></param>
        /// <param name="jwtService"></param>
        /// <param name="cryptographyService"></param>
        /// <param name="repository"></param>
        [SuppressMessage("ReSharper", "SuggestBaseTypeForParameterInConstructor")]
        public UserService(
            IUserTypeService userTypeService, 
            IMembershipService membershipService, 
            IRoleService roleService,
            IEventService eventService,
            IJwtService jwtService,
            ICryptographyService cryptographyService,
            IUserRepository repository) : base(repository)
        {
            this._userTypeService = userTypeService;
            this._membershipService = membershipService;
            this._roleService = roleService;
            this._eventService = eventService;
            this._jwtService = jwtService;
            this._cryptographyService = cryptographyService;
        }

        #endregion

        #region Events

        public event EventHandler<CreateResourceEventArgs<DynamicObject>> OnCreated;
        public event EventHandler<UpdateResourceEventArgs<DynamicObject>> OnUpdated;
        public event EventHandler<DeleteResourceEventArgs<DynamicObject>> OnDeleted;

        #endregion
        
        #region Event Methods

        private async Task FireOnCreatedEvent(string membershipId, Utilizer utilizer, DynamicObject inserted)
        {
            if (this._eventService != null)
            {
                await this._eventService.FireEventAsync(this, new ErtisAuthEvent
                {
                    Document = inserted.ToDynamic(),
                    Prior = null,
                    EventTime = DateTime.Now,
                    EventType = ErtisAuthEventType.UserCreated,
                    MembershipId = membershipId,
                    UtilizerId = utilizer.Id
                });	
            }
            
            this.OnCreated?.Invoke(this, new CreateResourceEventArgs<DynamicObject>(utilizer, inserted));
        }
		
        private async Task FireOnUpdatedEvent(string membershipId, Utilizer utilizer, DynamicObject prior, DynamicObject updated)
        {
            if (this._eventService != null)
            {
                await this._eventService.FireEventAsync(this, new ErtisAuthEvent
                {
                    Document = updated.ToDynamic(),
                    Prior = prior.ToDynamic(),
                    EventTime = DateTime.Now,
                    EventType = ErtisAuthEventType.UserUpdated,
                    MembershipId = membershipId,
                    UtilizerId = utilizer.Id
                });	
            }
            
            this.OnUpdated?.Invoke(this, new UpdateResourceEventArgs<DynamicObject>(utilizer, prior, updated));
        }
		
        private async Task FireOnDeletedEvent(string membershipId, Utilizer utilizer, DynamicObject deleted)
        {
            if (this._eventService != null)
            {
                await this._eventService.FireEventAsync(this, new ErtisAuthEvent
                {
                    Document = null,
                    Prior = deleted.ToDynamic(),
                    EventTime = DateTime.Now,
                    EventType = ErtisAuthEventType.UserDeleted,
                    MembershipId = membershipId,
                    UtilizerId = utilizer.Id
                });	
            }
            
            this.OnDeleted?.Invoke(this, new DeleteResourceEventArgs<DynamicObject>(utilizer, deleted));
        }

        #endregion

        #region Id Methods

        private void EnsureId(DynamicObject model)
        {
	        if (model.TryGetValue("_id", out string id, out _) && string.IsNullOrEmpty(id))
	        {
		        model.RemoveProperty("_id");
	        }
        }

        #endregion
        
        #region Membership Methods
        
        private async Task<Membership> CheckMembershipAsync(string membershipId, CancellationToken cancellationToken = default)
        {
            var membership = await this._membershipService.GetAsync(membershipId, cancellationToken: cancellationToken);
            if (membership == null)
            {
                throw ErtisAuthException.MembershipNotFound(membershipId);
            }

            return membership;
        }

        private void EnsureMembershipId(DynamicObject model, string membershipId)
        {
            model.SetValue("membership_id", membershipId, true);
        }

        #endregion

        #region UserType Methods

        private async Task<UserType> GetUserTypeAsync(DynamicObject model, string membershipId, bool fallbackWithOriginUserType = false, CancellationToken cancellationToken = default)
        {
	        if (model.TryGetValue("user_type", out string userTypeName, out _) && !string.IsNullOrEmpty(userTypeName))
            {
	            var userType = await this._userTypeService.GetByNameOrSlugAsync(membershipId, userTypeName, cancellationToken: cancellationToken);
	            if (userType == null)
	            {
		            throw ErtisAuthException.UserTypeNotFound(userTypeName, "name");
	            }

	            return userType;   
            }
            else if (fallbackWithOriginUserType)
            {
	            return await this._userTypeService.GetByNameOrSlugAsync(membershipId, UserType.ORIGIN_USER_TYPE_SLUG, cancellationToken: cancellationToken);
            }
	        else
	        {
		        throw ErtisAuthException.UserTypeRequired();
	        }
        }
        
        private async Task EnsureUserTypeAsync(string membershipId, UserType userType, DynamicObject model, string userId, string currentUserTypeSlug)
        {
            // Check IsAbstract
            if (userType.IsAbstract)
            {
                throw ErtisAuthException.InheritedTypeIsAbstract(userType.Name);
            }
                    
            // User type can not changed
            if (!string.IsNullOrEmpty(currentUserTypeSlug) && currentUserTypeSlug != userType.Slug)
            {
                throw ErtisAuthException.UserTypeImmutable();
            }

            // User model validation
            var validationContext = new FieldValidationContext(model);
            if (!userType.ValidateContent(model, validationContext) || !await this.CheckUniquePropertiesAsync(membershipId, userType, model, userId, validationContext))
            {
                throw new CumulativeValidationException(validationContext.Errors);
            }
        }

        private async Task<bool> CheckUniquePropertiesAsync(string membershipId, UserType userType, DynamicObject model, string userId, IValidationContext validationContext)
        {
            var isValid = true;
            var uniqueProperties = userType.GetUniqueProperties();
            foreach (var uniqueProperty in uniqueProperties)
            {
	            var path = uniqueProperty.GetSelfPath(userType);
	            if (model.TryGetValue(path, out var value, out _) && value != null)
                {
                    var found = await this.FindOneAsync(
                        QueryBuilder.Equals("membership_id", membershipId),
                        QueryBuilder.Equals(path, value));

                    if (found != null && found.TryGetValue(path, out var value_, out _) && value.Equals(value_))
                    {
                        if (string.IsNullOrEmpty(userId) || found.TryGetValue("_id", out string foundId, out _) && userId != foundId)
                        {
                            isValid = false;
                            validationContext.Errors.Add(new FieldValidationException($"The '{uniqueProperty.Name}' field has unique constraint. The same value is already using in another user.", uniqueProperty));   
                        }
                    }
                }
            }

            return isValid;
        }
        
        private void EnsureManagedProperties(DynamicObject model, string membershipId)
        {
	        model.RemoveProperty("_id");
	        model.RemoveProperty("password");
	        model.RemoveProperty("password_hash");
	        model.RemoveProperty("membership_id");
	        model.RemoveProperty("sys");
	        
	        model.SetValue("membership_id", membershipId, true);
        }
        
        #endregion

        #region Role Methods

        private async Task EnsureRoleAsync(DynamicObject model, string membershipId, CancellationToken cancellationToken = default)
        {
	        var roleName = model.GetValue<string>("role");
	        if (string.IsNullOrEmpty(roleName))
	        {
		        throw ErtisAuthException.RoleRequired();
	        }
	        
	        var role = await this._roleService.GetByNameAsync(roleName, membershipId, cancellationToken: cancellationToken);
	        if (role == null)
	        {
		        throw ErtisAuthException.RoleNotFound(roleName, true);
	        }
        }

        #endregion

        #region Ubac Methods

        private void EnsureUbacs(DynamicObject model)
        {
	        var permissionList = new List<Ubac>();
	        if (model.TryGetValue("permissions", out string[] permissions, out _) && permissions != null)
	        {
		        foreach (var permission in permissions)
		        {
			        var ubac = Ubac.Parse(permission);
			        permissionList.Add(ubac);
		        }
	        }
				
	        var forbiddenList = new List<Ubac>();
	        if (model.TryGetValue("forbidden", out string[] forbiddens, out _) && forbiddens != null)
	        {
		        foreach (var forbidden in forbiddens)
		        {
			        var ubac = Ubac.Parse(forbidden);
			        forbiddenList.Add(ubac);
		        }
	        }
				
	        // Is there any conflict?
	        foreach (var permissionUbac in permissionList)
	        {
		        foreach (var forbiddenUbac in forbiddenList)
		        {
			        if (permissionUbac == forbiddenUbac)
			        {
				        throw ErtisAuthException.UbacsConflicted($"Permitted and forbidden sets are conflicted. The same permission is there in the both set. ('{permissionUbac}')");
			        }
		        }	
	        }
        }

        #endregion

        #region Reference Methods

        private async Task EmbedReferencesAsync(UserType userType, DynamicObject model, CancellationToken cancellationToken = default)
        {
            var referenceProperties = userType.GetReferenceProperties();
            foreach (var referenceProperty in referenceProperties)
            {
	            var path = referenceProperty.GetSelfPath(userType);

                // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                switch (referenceProperty.ReferenceType)
                {
                    case ReferenceFieldInfo.ReferenceTypes.single:
                    {
                        if (model.TryGetValue(path, out var value, out _) && value is string referenceId && !string.IsNullOrEmpty(referenceId))
                        {
                            var referenceItem = await this.GetAsync(userType.MembershipId, referenceId, cancellationToken: cancellationToken);
                            if (referenceItem != null)
                            {
                                if (!string.IsNullOrEmpty(referenceProperty.ContentType))
                                {
                                    if (referenceItem.TryGetValue("user_type", out string referenceItemUserType, out _))
                                    {
                                        if (await this._userTypeService.IsInheritFromAsync(userType.MembershipId, referenceItemUserType, referenceProperty.ContentType, cancellationToken: cancellationToken))
                                        {
                                            model.TrySetValue(path, referenceItem.ToDynamic(), out Exception _);   
                                        }
                                        else
                                        {
                                            throw new FieldValidationException(
                                                $"This reference-type field only can bind contents from '{referenceProperty.ContentType}' content-type or inherited from '{referenceProperty.ContentType}' content-type. ('{referenceProperty.Name}')",
                                                referenceProperty);
                                        }
                                    }
                                    else
                                    {
                                        throw new FieldValidationException(
                                            $"Content type could not read for reference value '{referenceProperty.Name}'",
                                            referenceProperty);
                                    }
                                }
                            }
                            else
                            {
                                throw new FieldValidationException(
                                    $"Could not find any content with id '{referenceId}' for reference type '{referenceProperty.Name}'",
                                    referenceProperty);
                            }
                        }

                        break;
                    }
                    case ReferenceFieldInfo.ReferenceTypes.multiple:
                    {
                        if (model.TryGetValue(path, out var value, out _) && value is object[] referenceObjectIds && referenceObjectIds.Any() && referenceObjectIds.All(x => x is string))
                        {
                            var referenceIds = referenceObjectIds.Cast<string>();
                            var referenceItems = new List<object>();
                            foreach (var referenceId in referenceIds)
                            {
                                var referenceItem = await this.GetAsync(userType.MembershipId, referenceId, cancellationToken: cancellationToken);
                                if (referenceItem != null)
                                {
                                    if (!string.IsNullOrEmpty(referenceProperty.ContentType))
                                    {
                                        if (referenceItem.TryGetValue("user_type", out string referenceItemUserType, out _))
                                        {
                                            if (await this._userTypeService.IsInheritFromAsync(userType.MembershipId, referenceItemUserType, referenceProperty.ContentType, cancellationToken: cancellationToken))
                                            {
                                                referenceItems.Add(referenceItem.ToDynamic());
                                            }
                                            else
                                            {
                                                throw new FieldValidationException(
                                                    $"This reference-type field only can bind contents from '{referenceProperty.ContentType}' content-type or inherited from '{referenceProperty.ContentType}' content-type. ('{referenceProperty.Name}')",
                                                    referenceProperty);
                                            }
                                        }
                                        else
                                        {
                                            throw new FieldValidationException(
                                                $"Content type could not read for reference value '{referenceProperty.Name}'",
                                                referenceProperty);
                                        }
                                    }
                                }
                                else
                                {
                                    throw new FieldValidationException(
                                        $"Could not find any content with id '{referenceId}' for reference type '{referenceProperty.Name}'",
                                        referenceProperty);
                                }
                            }
                            
                            model.TrySetValue(path, referenceItems.ToArray(), out _);
                        }

                        break;
                    }
                }
            }
        }

        #endregion

        #region Sys Methods

        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void EnsureSys(DynamicObject model, Utilizer utilizer)
        {
            var now = DateTime.Now.ToLocalTime().Add(TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow));
            var utilizerName = utilizer.Username;
            if (utilizer.Type == Utilizer.UtilizerType.System)
            {
	            utilizerName = "system";
            }

            if (model.TryGetValue<SysModel>("sys", out var sys, out _) && sys != null)
            {
                sys.CreatedAt ??= now;
                sys.CreatedBy ??= utilizerName;
                sys.ModifiedAt = now;
                sys.ModifiedBy = utilizerName;
            }
            else
            {
                sys = new SysModel
                {
                    CreatedAt = now,
                    CreatedBy = utilizerName,
                };
            }

            model.SetValue("sys", sys.ToDictionary(), true);
        }

        #endregion
        
        #region Password Methods

        // ReSharper disable once MemberCanBeMadeStatic.Local
        private string GetPassword(DynamicObject model)
        {
	        model.TryGetValue("password", out string password, out _);
	        return password;
        }
        
        private void EnsurePassword(DynamicObject model, out string password)
        {
	        password = this.GetPassword(model);
	        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(password.Trim()))
	        {
		        throw ErtisAuthException.PasswordRequired();
	        }
	        else if (password.Length < 6)
	        {
		        throw ErtisAuthException.PasswordMinLengthRuleError(6);
	        }
        }
        
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void SetPasswordHash(DynamicObject model, Membership membership, string password)
        {
	        if (!string.IsNullOrEmpty(password))
	        {
		        model.SetValue("password_hash", this._cryptographyService.CalculatePasswordHash(membership, password), true);
	        }
        }
        
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void HidePasswordHash(DynamicObject model)
        {
	        model.RemoveProperty("password_hash");
        }

        #endregion

        #region Provider Methods

        private KnownProviders GetSourceProvider(DynamicObject model)
        {
	        try
	        {
		        if (!model.ContainsProperty("sourceProvider"))
		        {
			        return KnownProviders.ErtisAuth;
		        }
		        
		        var sourceProviderName = model.GetValue<string>("sourceProvider");
		        return Enum.Parse<KnownProviders>(sourceProviderName);
	        }
	        catch
	        {
		        return KnownProviders.ErtisAuth;
	        }
        }

        #endregion
        
        #region Read Methods
        
        public async Task<DynamicObject> GetAsync(string membershipId, string id, CancellationToken cancellationToken = default)
        {
            await this.CheckMembershipAsync(membershipId, cancellationToken: cancellationToken);
            return await base.FindOneAsync(QueryBuilder.Equals("membership_id", membershipId), QueryBuilder.ObjectId(id));
        }
        
        public async Task<IPaginationCollection<DynamicObject>> GetAsync(
            string membershipId,
            int? skip = null, 
            int? limit = null, 
            bool withCount = false, 
            string orderBy = null,
            SortDirection? sortDirection = null, 
            CancellationToken cancellationToken = default)
        {
            await this.CheckMembershipAsync(membershipId, cancellationToken: cancellationToken);
            var queries = new[]
            {
                QueryBuilder.Equals("membership_id", membershipId)
            };
                
            return await base.GetAsync(queries, skip, limit, withCount, orderBy, sortDirection, cancellationToken: cancellationToken);
        }

        public async Task<IPaginationCollection<DynamicObject>> QueryAsync(
            string membershipId,
            string query, 
            int? skip = null, 
            int? limit = null, 
            bool? withCount = null,
            string orderBy = null, 
            SortDirection? sortDirection = null, 
            IDictionary<string, bool> selectFields = null, 
            CancellationToken cancellationToken = default)
        {
            await this.CheckMembershipAsync(membershipId, cancellationToken: cancellationToken);
            query = Helpers.QueryHelper.InjectMembershipIdToQuery<dynamic>(query, membershipId);
            return await base.QueryAsync(query, skip, limit, withCount, orderBy, sortDirection, selectFields, cancellationToken: cancellationToken);
        }

        public async Task<IPaginationCollection<DynamicObject>> SearchAsync(
	        string membershipId,
	        string keyword,
	        int? skip = null,
	        int? limit = null,
	        bool? withCount = null,
	        string orderBy = null,
	        SortDirection? sortDirection = null, 
	        CancellationToken cancellationToken = default)
        {
	        await this.CheckMembershipAsync(membershipId, cancellationToken: cancellationToken);
	        var query = QueryBuilder.And(QueryBuilder.Equals("membership_id", membershipId), QueryBuilder.FullTextSearch(keyword)).ToString();
	        return await base.QueryAsync(query, skip, limit, withCount, orderBy, sortDirection, cancellationToken: cancellationToken);
        }
        
        public async Task<UserWithPasswordHash> GetUserWithPasswordAsync(string membershipId, string id, CancellationToken cancellationToken = default)
        {
	        var dynamicObject = await this.GetAsync(membershipId, id, cancellationToken: cancellationToken);
	        return dynamicObject?.Deserialize<UserWithPasswordHash>();
        }
        
        public async Task<UserWithPasswordHash> GetUserWithPasswordAsync(string membershipId, string username, string email, CancellationToken cancellationToken = default)
        {
	        var dynamicObject = await this.FindOneAsync(
		        QueryBuilder.And(
			        QueryBuilder.Equals("membership_id", membershipId), 
			        QueryBuilder.Or(
				        QueryBuilder.Equals("username", username),
				        QueryBuilder.Equals("email_address", email),
				        QueryBuilder.Equals("username", email),
				        QueryBuilder.Equals("email_address", username)
				    )
			    )
		    );
	        
	        return dynamicObject?.Deserialize<UserWithPasswordHash>();
        }
        
        #endregion

        #region Validation Methods

        private async Task EnsureAndValidateAsync(
	        Utilizer utilizer, 
	        string membershipId, 
	        string id, 
	        UserType userType,
	        DynamicObject model, 
	        DynamicObject current, 
	        CancellationToken cancellationToken = default)
        {
	        this.EnsureMembershipId(model, membershipId);
	        this.EnsureId(model);
	        this.EnsureSys(model, utilizer);
	        this.EnsureUbacs(model);
	        
	        await this.EnsureUserTypeAsync(membershipId, userType, model, id, current?.GetValue<string>("user_type"));
	        await this.EmbedReferencesAsync(userType, model, cancellationToken: cancellationToken);
	        await this.EnsureRoleAsync(model, membershipId, cancellationToken: cancellationToken);
        }

        #endregion
        
        #region Create Methods
        
        public async Task<DynamicObject> CreateAsync(Utilizer utilizer, string membershipId, DynamicObject model, CancellationToken cancellationToken = default)
        {
	        var membership = await this.CheckMembershipAsync(membershipId, cancellationToken: cancellationToken);
	        
	        string password = null;
	        var sourceProvider = this.GetSourceProvider(model);
	        if (sourceProvider == KnownProviders.ErtisAuth)
	        {
		        this.EnsurePassword(model, out password);    
	        }
	        
	        var userType = await this.GetUserTypeAsync(model, membershipId, sourceProvider == KnownProviders.ErtisAuth, cancellationToken: cancellationToken);
	        this.EnsureManagedProperties(model, membershipId);
	        await this.EnsureAndValidateAsync(utilizer, membershipId, null, userType, model, null, cancellationToken: cancellationToken);
	        
	        if (sourceProvider == KnownProviders.ErtisAuth)
	        {
		        this.SetPasswordHash(model, membership, password);    
	        }
	        
	        var created = await base.CreateAsync(model, cancellationToken: cancellationToken);
            this.HidePasswordHash(created);
            if (created != null)
            {
                await this.FireOnCreatedEvent(membershipId, utilizer, created);
            }
            
            return created;
        }

        #endregion
        
        #region Update Methods

        public async Task<DynamicObject> UpdateAsync(Utilizer utilizer, string membershipId, string userId, DynamicObject model, bool fireEvent = true, CancellationToken cancellationToken = default)
        {
	        await this.CheckMembershipAsync(membershipId, cancellationToken: cancellationToken);
	        var userType = await this.GetUserTypeAsync(model, membershipId, true, cancellationToken: cancellationToken);
	        this.EnsureManagedProperties(model, membershipId);
	        model = this.SyncModel(membershipId, userId, model, out var current);
	        await this.EnsureAndValidateAsync(utilizer, membershipId, userId, userType, model, current, cancellationToken: cancellationToken);
	        var updated = await base.UpdateAsync(userId, model, cancellationToken: cancellationToken);
	        this.HidePasswordHash(updated);
	        if (updated != null && fireEvent)
	        {
		        await this.FireOnUpdatedEvent(membershipId, utilizer, current, updated);
	        }
            
	        return updated;
        }
        
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private DynamicObject SyncModel(string membershipId, string userId, DynamicObject model, out DynamicObject current)
        {
	        current = this.GetAsync(membershipId, userId).ConfigureAwait(false).GetAwaiter().GetResult();
	        if (current == null)
	        {
		        throw ErtisAuthException.UserNotFound(userId, "_id");
	        }
	        
	        model = current.Merge(model);
	        model.RemoveProperty("_id");

	        return model;
        }

        #endregion
        
        #region Delete Methods

        public bool Delete(Utilizer utilizer, string membershipId, string id) =>
	        this.DeleteAsync(utilizer, membershipId, id).ConfigureAwait(false).GetAwaiter().GetResult();
        
        public async ValueTask<bool> DeleteAsync(Utilizer utilizer, string membershipId, string id, CancellationToken cancellationToken = default)
        {
            var current = await this.GetAsync(membershipId, id, cancellationToken: cancellationToken);
            if (current == null)
            {
                throw ErtisAuthException.UserNotFound(id, "_id");
            }
            
            await this.CheckMembershipAsync(membershipId, cancellationToken: cancellationToken);
            
            var isDeleted = await base.DeleteAsync(id, cancellationToken: cancellationToken);
            if (isDeleted)
            {
                await this.FireOnDeletedEvent(membershipId, utilizer, current);
            }
            
            return isDeleted;
        }

        public bool? BulkDelete(Utilizer utilizer, string membershipId, string[] ids) =>
			this.BulkDeleteAsync(utilizer, membershipId, ids).ConfigureAwait(false).GetAwaiter().GetResult();
        
        public async ValueTask<bool?> BulkDeleteAsync(Utilizer utilizer, string membershipId, string[] ids, CancellationToken cancellationToken = default)
        {
	        await this.CheckMembershipAsync(membershipId, cancellationToken: cancellationToken);

	        var isAllDeleted = true;
	        var isAllFailed = true;
	        foreach (var id in ids)
	        {
		        var isDeleted = await base.DeleteAsync(id, cancellationToken: cancellationToken);
		        isAllDeleted &= isDeleted;
		        isAllFailed &= !isDeleted;
	        }

	        if (isAllDeleted)
	        {
		        return true;
	        }
	        else if (isAllFailed)
	        {
		        return false;
	        }
	        else
	        {
		        return null;
	        }
        }

        #endregion

		#region Change Password

		public async Task<DynamicObject> ChangePasswordAsync(Utilizer utilizer, string membershipId, string userId, string newPassword, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrEmpty(newPassword))
			{
				throw ErtisAuthException.ValidationError(new []
				{
					"Password can not be null or empty!"
				});
			}
			
			var membership = await this._membershipService.GetAsync(membershipId, cancellationToken: cancellationToken);
			if (membership == null)
			{
				throw ErtisAuthException.MembershipNotFound(membershipId);
			}

			var user = await this.GetUserWithPasswordAsync(membershipId, userId, cancellationToken: cancellationToken);
			if (user == null)
			{
				throw ErtisAuthException.UserNotFound(userId, "_id");
			}

			var dynamicObject = new DynamicObject(user);
			this.EnsureManagedProperties(dynamicObject, membershipId);
			dynamicObject = this.SyncModel(membershipId, userId, dynamicObject, out _);
			
			var passwordHash = this._cryptographyService.CalculatePasswordHash(membership, newPassword);
			dynamicObject.SetValue("password_hash", passwordHash, true);

			var updatedUser = await base.UpdateAsync(userId, dynamicObject, cancellationToken: cancellationToken);
			await this._eventService.FireEventAsync(this, new ErtisAuthEvent
			{
				EventType = ErtisAuthEventType.UserPasswordChanged,
				UtilizerId = user.Id,
				Document = updatedUser,
				Prior = user,
				MembershipId = membershipId
			}, cancellationToken: cancellationToken);

			return updatedUser;
		}

		#endregion

		#region Forgot Password

		public async Task<ResetPasswordToken> ResetPasswordAsync(Utilizer utilizer, string membershipId, string emailAddress, string server, string host, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrEmpty(emailAddress))
			{
				throw ErtisAuthException.ValidationError(new []
				{
					"Username or email required!"
				});
			}
			
			var membership = await this._membershipService.GetAsync(membershipId, cancellationToken: cancellationToken);
			if (membership == null)
			{
				throw ErtisAuthException.MembershipNotFound(membershipId);
			}

			var user = await this.GetUserWithPasswordAsync(membershipId, emailAddress, emailAddress, cancellationToken: cancellationToken);
			if (user == null)
			{
				throw ErtisAuthException.UserNotFound(emailAddress, "email_address");
			}

			if (utilizer.Role is ReservedRoles.Administrator or ReservedRoles.Server || utilizer.Id == user.Id)
			{
				var tokenClaims = new TokenClaims(Guid.NewGuid().ToString(), user, membership);
				tokenClaims.AddClaim("token_type", "reset_token");
				var resetToken = this._jwtService.GenerateToken(tokenClaims, HashAlgorithms.SHA2_256, Encoding.UTF8);
				var resetPasswordToken = new ResetPasswordToken(resetToken, TimeSpan.FromHours(1));

				var resetPasswordLink = GenerateResetPasswordTokenMailLink(
					emailAddress, 
					resetPasswordToken.Token, 
					membershipId,
					membership.SecretKey, 
					server, 
					host);

				var eventPayload = new
				{
					resetPasswordToken,
					resetPasswordLink,
					user,
					membership
				};
				
				await this._eventService.FireEventAsync(this, new ErtisAuthEvent
				{
					EventType = ErtisAuthEventType.UserPasswordReset,
					UtilizerId = user.Id,
					Document = eventPayload,
					MembershipId = membershipId
				}, cancellationToken: cancellationToken);

				return resetPasswordToken;
			}
			else
			{
				throw ErtisAuthException.AccessDenied("Unauthorized access");
			}
		}

		private static string GenerateResetPasswordTokenMailLink(string emailAddress, string resetPasswordToken, string membershipId, string secretKey, string serverUrl, string host)
		{
			var encryptedResetPasswordToken = Identity.Cryptography.StringCipher.Encrypt(resetPasswordToken, membershipId);
			var encryptedSecretKey = Identity.Cryptography.StringCipher.Encrypt(secretKey, membershipId);
			var payloadDictionary = new Dictionary<string, string>
			{
				{ "emailAddress", emailAddress },
				{ "encryptedSecretKey", encryptedSecretKey },
				{ "serverUrl", serverUrl },
				{ "membershipId", membershipId },
				{ "encryptedResetPasswordToken", encryptedResetPasswordToken },
			};

			var encodedPayload = Identity.Cryptography.StringCipher.Encrypt(string.Join('&', payloadDictionary.Select(x => $"{x.Key}={x.Value}")), membershipId);
			var resetPasswordPayload = $"{membershipId}:{encodedPayload}";
			var urlEncodedPayload = System.Web.HttpUtility.UrlEncode(resetPasswordPayload, Encoding.ASCII);
			var resetPasswordLink = $"https://{host}/set-password?token={urlEncodedPayload}";
			return resetPasswordLink;
		}

		public async Task SetPasswordAsync(Utilizer utilizer, string membershipId, string resetToken, string usernameOrEmailAddress, string password, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrEmpty(usernameOrEmailAddress))
			{
				throw ErtisAuthException.ValidationError(new []
				{
					"Username or email required!"
				});
			}
			
			var membership = await this._membershipService.GetAsync(membershipId, cancellationToken: cancellationToken);
			if (membership == null)
			{
				throw ErtisAuthException.MembershipNotFound(membershipId);
			}

			var user = await this.GetUserWithPasswordAsync(membershipId, usernameOrEmailAddress, usernameOrEmailAddress, cancellationToken: cancellationToken);
			if (user == null)
			{
				throw ErtisAuthException.UserNotFound(usernameOrEmailAddress, "username or email_address");
			}

			if (utilizer.Role is ReservedRoles.Administrator or ReservedRoles.Server || utilizer.Id == user.Id)
			{
				if (this._jwtService.TryDecodeToken(resetToken, out var securityToken))
				{
					var expireTime = securityToken.ValidTo.ToLocalTime();
					if (DateTime.Now > expireTime)
					{
						// Token was expired!
						throw ErtisAuthException.TokenWasExpired();	
					}

					await this.ChangePasswordAsync(utilizer, membershipId, user.Id, password, cancellationToken: cancellationToken);
				}
				else
				{
					// Reset token could not decoded!
					throw ErtisAuthException.InvalidToken();
				}
			}
			else
			{
				throw ErtisAuthException.AccessDenied("Unauthorized access");
			}
		}

		#endregion

		#region Check Password

		public async Task<bool> CheckPasswordAsync(Utilizer utilizer, string password, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrEmpty(password))
			{
				return false;
			}
			
			var membership = await this._membershipService.GetAsync(utilizer.MembershipId, cancellationToken: cancellationToken);
			if (membership == null)
			{
				throw ErtisAuthException.MembershipNotFound(utilizer.MembershipId);
			}

			var user = await this.GetUserWithPasswordAsync(utilizer.MembershipId, utilizer.Id, cancellationToken: cancellationToken);
			if (user == null)
			{
				throw ErtisAuthException.UserNotFound(utilizer.Id, "_id");
			}

			var passwordHash = this._cryptographyService.CalculatePasswordHash(membership, password);
			return user.PasswordHash == passwordHash;
		}
		
		#endregion
    }
}