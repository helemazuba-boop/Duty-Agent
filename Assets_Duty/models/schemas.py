from typing import Any, Dict, List, Optional

from pydantic import BaseModel, ConfigDict


class DutyRequest(BaseModel):
    # Keep extra fields instead of silently dropping host payload fields.
    model_config = ConfigDict(extra="allow")

    instruction: str
    apply_mode: Optional[str] = None
    api_key: Optional[str] = None
    config: Optional[Dict[str, Any]] = None

    # Common top-level fields sent by C# host.
    per_day: Optional[int] = None
    duty_rule: Optional[str] = None
    base_url: Optional[str] = None
    prompt_mode: Optional[str] = None
    model: Optional[str] = None
    llm_stream: Optional[bool] = None
    stream: Optional[bool] = None
    area_names: Optional[List[str]] = None
    area_per_day_counts: Optional[Dict[str, int]] = None
    existing_notes: Optional[Dict[str, str]] = None
