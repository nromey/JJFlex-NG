# SmartLink Accounts

JJ Flex can remember more than one SmartLink account. If you've got a club radio and a home radio on separate Flex accounts, or you help a friend and occasionally log into theirs, you don't have to sign out and sign in every time. Save each account once, pick which one to use when you connect.

## Saving an account

First time you sign into a SmartLink account in JJ Flex, the credentials get saved automatically — the Auth0 ID token and the refresh token, both encrypted on disk using Windows DPAPI (so they can't be decrypted on a different user account or a different machine).

You can give each saved account a friendly name so you don't have to remember email addresses: something like "Home shack" or "Club K4ABC". The account manager dialog lets you rename any saved account at any time.

## Switching between accounts

Radio menu > Manage SmartLink Accounts opens the account manager. You see every saved account, their friendly names, and the email address underneath each. Pick the one you want, confirm, and JJ Flex logs you in to that account and fetches its radio list.

No password re-entry as long as the refresh token is still valid (several months). If it expires, JJ Flex will prompt you through the Auth0 sign-in again and replace the stored credentials.

## Deleting an account

Same dialog. Pick the account, choose Delete, confirm. The stored tokens are erased from disk. JJ Flex will never auto-sign-in to a deleted account.

## Where this matters

- Club radios where multiple ops log in. Each op keeps their own saved account; the radio is shared but the accounts aren't.
- Home + portable setups. Save the Auth0 credentials for both accounts once; switch by name, not by typing.
- Troubleshooting. If a stored credential gets corrupted or stale, delete the account and re-add it — clean slate without disturbing other accounts.

## Security note

All stored tokens are DPAPI-encrypted. If someone copies your `SmartLinkAccounts.json` file to a different machine or different Windows user, they can't decrypt it. The cleartext tokens only live in memory while JJ Flex is running. If you're uncomfortable with any long-lived credential storage at all, JJ Flex will still work without the account manager — you just have to go through Auth0's sign-in flow every session.
