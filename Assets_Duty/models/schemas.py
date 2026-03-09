from pydantic import BaseModel, Field
from typing import List, Optional, Any

class DutyRequest(BaseModel):
    instruction: str
    apply_mode: str = "append"
    per_day: int = 2
    duty_rule: Optional[str] = None
    base_url: Optional[str] = None
    prompt_mode: str = "Regular"
    model: str = "gpt-4o"
    api_key: str
    proxy: Optional[str] = None
    start_date: Optional[str] = None
    end_date: Optional[str] = None

class CoreProgressResponse(BaseModel):
    phase: str
    message: str
    stream_chunk: Optional[str] = None

class CoreCompleteResponse(BaseModel):
    status: str
    message: str
    ai_response: Optional[str] = None
