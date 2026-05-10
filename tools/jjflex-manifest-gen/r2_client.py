"""Cloudflare R2 client wrapper.

Thin shim over boto3's S3 client pointed at R2's S3-compatible endpoint.
Kept separate from manifest_gen.py so tests can mock the upload surface
without dragging in network or boto3 itself.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True)
class R2Config:
    """R2 connection parameters loaded from environment variables."""

    account_id: str
    access_key_id: str
    secret_access_key: str
    bucket: str

    @property
    def endpoint_url(self) -> str:
        return f"https://{self.account_id}.r2.cloudflarestorage.com"


def load_r2_config(bucket_override: Optional[str] = None) -> R2Config:
    """Read R2 credentials from the standard env vars.

    Raises RuntimeError listing every missing var so callers get a single
    actionable error instead of one-at-a-time KeyErrors.
    """
    required = {
        "R2_ACCOUNT_ID": os.environ.get("R2_ACCOUNT_ID"),
        "R2_ACCESS_KEY_ID": os.environ.get("R2_ACCESS_KEY_ID"),
        "R2_SECRET_ACCESS_KEY": os.environ.get("R2_SECRET_ACCESS_KEY"),
    }
    bucket = bucket_override or os.environ.get("R2_BUCKET")
    if not bucket:
        required["R2_BUCKET"] = None

    missing = [k for k, v in required.items() if not v]
    if missing:
        raise RuntimeError(
            "Missing R2 credentials in environment: "
            + ", ".join(missing)
            + ". See README.md for setup."
        )

    return R2Config(
        account_id=required["R2_ACCOUNT_ID"],
        access_key_id=required["R2_ACCESS_KEY_ID"],
        secret_access_key=required["R2_SECRET_ACCESS_KEY"],
        bucket=bucket,
    )


class R2Client:
    """Wraps boto3 S3 client + the bucket so callers don't repeat the bucket name."""

    def __init__(self, config: R2Config):
        import boto3
        from botocore.config import Config as BotoConfig

        self._config = config
        self._client = boto3.client(
            "s3",
            endpoint_url=config.endpoint_url,
            aws_access_key_id=config.access_key_id,
            aws_secret_access_key=config.secret_access_key,
            region_name="auto",
            config=BotoConfig(signature_version="s3v4", retries={"max_attempts": 3}),
        )
        self.bucket = config.bucket

    def head(self, key: str) -> Optional[dict]:
        """Return head_object response if key exists, else None."""
        from botocore.exceptions import ClientError

        try:
            return self._client.head_object(Bucket=self.bucket, Key=key)
        except ClientError as exc:
            if exc.response.get("Error", {}).get("Code") in ("404", "NoSuchKey", "NotFound"):
                return None
            raise

    def put(
        self,
        key: str,
        body: bytes,
        content_type: str = "application/octet-stream",
        cache_control: Optional[str] = None,
        metadata: Optional[dict] = None,
        if_match: Optional[str] = None,
    ) -> dict:
        kwargs = {
            "Bucket": self.bucket,
            "Key": key,
            "Body": body,
            "ContentType": content_type,
        }
        if cache_control:
            kwargs["CacheControl"] = cache_control
        if metadata:
            kwargs["Metadata"] = metadata
        if if_match:
            kwargs["IfMatch"] = if_match
        return self._client.put_object(**kwargs)

    def get_bytes(self, key: str) -> Optional[bytes]:
        """Fetch object body or None if missing."""
        from botocore.exceptions import ClientError

        try:
            resp = self._client.get_object(Bucket=self.bucket, Key=key)
            return resp["Body"].read()
        except ClientError as exc:
            if exc.response.get("Error", {}).get("Code") in ("404", "NoSuchKey", "NotFound"):
                return None
            raise
