"""multi_agent package.

NOTE: ``executor`` is imported lazily (PEP 562 module ``__getattr__``) on
purpose. An eager ``from .executor import ...`` here creates a circular
import: executor -> prompt_gateway -> multi_agent.prompts -> (this package
__init__) -> executor, which made a plain ``import single_pass_executor``
(or ``import prompt_gateway``) fail with "cannot import name
'build_agent_prompt' from partially initialized module". Existing tests only
passed because they happened to import ``engine`` first.
"""

__all__ = ["run_multi_agent_schedule"]


def __getattr__(name):
    if name == "run_multi_agent_schedule":
        from .executor import run_multi_agent_schedule

        return run_multi_agent_schedule
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")
