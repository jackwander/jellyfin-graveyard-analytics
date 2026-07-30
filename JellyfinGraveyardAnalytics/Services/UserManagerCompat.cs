using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinGraveyardAnalytics.Services
{
    /// <summary>
    /// Enumerates the server's users across the 10.11.x line, which changed how to ask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IUserManager.Users</c> existed through 10.11.8 and was replaced by
    /// <c>IUserManager.GetUsers()</c> in 10.11.9. The two never overlap: each version has one
    /// and not the other, both returning <see cref="IEnumerable{T}"/> of <see cref="User"/>.
    /// So no single compiled call reaches both, and a plugin that compiles against 10.11.6 and
    /// calls the property ships IL referencing <c>get_Users</c> — a member that no longer
    /// exists on a current server. It loads (the manifest's <c>targetAbi</c> is a *minimum*)
    /// and then fails when the Guestbook is opened.
    /// </para>
    /// <para>
    /// Resolving by name, once, is what keeps one artifact working on every 10.11.x. It also
    /// means this file must not name either member in code — a compile-time reference to
    /// either one would reintroduce exactly the dependency it exists to remove.
    /// </para>
    /// <para>
    /// Guarded by <c>tests/harness/dotnet/abi</c>, which reads every Jellyfin member the built
    /// assembly's IL references and resolves it against several 10.11.x releases.
    /// </para>
    /// </remarks>
    internal static class UserManagerCompat
    {
        private const string MethodName = "GetUsers";
        private const string PropertyName = "Users";

        /// <summary>
        /// Resolved once per process: which accessor this server's assembly actually offers.
        /// </summary>
        private static readonly Func<IUserManager, IEnumerable<User>> Accessor = Resolve();

        /// <summary>
        /// Every user known to the server.
        /// </summary>
        /// <param name="userManager">The server's user manager.</param>
        /// <returns>The users.</returns>
        public static IEnumerable<User> AllUsers(IUserManager userManager)
        {
            ArgumentNullException.ThrowIfNull(userManager);
            return Accessor(userManager);
        }

        private static Func<IUserManager, IEnumerable<User>> Resolve()
        {
            // 10.11.9 and later.
            var method = typeof(IUserManager).GetMethod(MethodName, Type.EmptyTypes);
            if (method is not null && typeof(IEnumerable<User>).IsAssignableFrom(method.ReturnType))
            {
                return Bind(method);
            }

            // 10.11.8 and earlier.
            var getter = typeof(IUserManager).GetProperty(PropertyName)?.GetGetMethod();
            if (getter is not null && typeof(IEnumerable<User>).IsAssignableFrom(getter.ReturnType))
            {
                return Bind(getter);
            }

            // Neither shape is present, so this is a server whose IUserManager this plugin has
            // never seen. Failing here names the cause; letting it through would surface as a
            // NullReferenceException somewhere in the Guestbook.
            throw new MissingMemberException(
                nameof(IUserManager),
                $"{MethodName}() or {PropertyName}");
        }

        // An open delegate over an interface method: the receiver is the delegate's argument,
        // and the call still dispatches virtually on whatever implementation is passed in.
        private static Func<IUserManager, IEnumerable<User>> Bind(MethodInfo accessor)
            => (Func<IUserManager, IEnumerable<User>>)Delegate.CreateDelegate(
                typeof(Func<IUserManager, IEnumerable<User>>), accessor);
    }
}
