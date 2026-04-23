# SmartLink Accounts

JJ Flexible Radio Access can remember more than one SmartLink account. If you have a club radio and a home radio on separate Flex accounts, or if you help a friend and occasionally log into their account, you do not have to sign out and sign in every time. Save each account once, and then pick which one to use when you connect.

## Saving an Account

The first time you sign into a SmartLink account from JJ Flexible Radio Access, the credentials are saved automatically. Both the Auth0 ID token and the refresh token are stored encrypted on disk, using Windows DPAPI — which means they cannot be decrypted on a different user account or on a different machine.

You can give each saved account a friendly name, so you do not have to remember email addresses. Something like "Home shack" or "Club K4ABC" works well. The account manager dialog lets you rename any saved account at any time.

## Switching Between Accounts

From the **Radio** menu, open **Manage SmartLink Accounts**. The account manager dialog shows every account you have saved, along with each account's friendly name and its email address underneath. Pick the account you want to use, confirm the switch, and JJ Flexible Radio Access logs you in to that account and fetches its radio list.

There is no password re-entry as long as the refresh token is still valid, which typically means several months. If the refresh token has expired, JJ Flexible Radio Access will prompt you through the Auth0 sign-in again and replace the stored credentials with the new ones.

## Deleting an Account

From the same Manage SmartLink Accounts dialog, pick the account you want to remove, choose Delete, and confirm. The stored tokens are erased from disk. JJ Flexible Radio Access will never auto-sign-in to a deleted account.

## Where This Matters

- **Club radios where multiple operators log in.** Each operator keeps their own saved account; the radio itself is shared, but the accounts are not.
- **Home plus portable setups.** Save the Auth0 credentials for both accounts once, and switch between them by name rather than by typing credentials.
- **Troubleshooting.** If a stored credential gets corrupted or goes stale, delete that one account and re-add it from scratch — a clean slate, without disturbing any of the other accounts you have saved.

## Security Note

All stored tokens are DPAPI-encrypted. If someone copies your `SmartLinkAccounts.json` file to a different machine, or to a different Windows user account on the same machine, they cannot decrypt it. The cleartext tokens only ever live in memory while JJ Flexible Radio Access is actually running.

If you are uncomfortable with any long-lived credential storage at all, JJ Flexible Radio Access will still work without the account manager — you just have to go through the Auth0 sign-in flow once per session.
