from ..agent import Agent
from ..chat_history import ChatHistoryReadOnly
from ..termination import NotTerminated, Terminated, Termination, TerminationReason, TerminationResult
from ..types import AssistantMessage, UserMessage


class DefaultTermination(Termination):
    def __init__(self, *, termination_message: str = "TERMINATE", max_turns: int = 10) -> None:
        self._termination_message = termination_message
        self._max_turns = max_turns
        self._turns = 0

    def record_turn_taken(self, agent: Agent) -> None:
        self._turns += 1

    async def check_termination(self, chat_history: ChatHistoryReadOnly) -> TerminationResult:
        if self._turns >= self._max_turns:
            return Terminated(TerminationReason.MAX_TURNS_REACHED, "Max turns reached.")

        # TODO handle tool message
        for message in chat_history.messages:
            if isinstance(message, UserMessage):
                # TODO handle multimodal list of str/image type
                if isinstance(message.content, str) and self._termination_message in message.content:
                    return Terminated(TerminationReason.TERMINATION_MESSAGE, "Termination message received.")
                if message.is_termination:
                    return Terminated(TerminationReason.USER_REQUESTED, "User requested termination.")
                elif self._termination_message in message.content:
                    return Terminated(TerminationReason.TERMINATION_MESSAGE, "Termination message received.")
            if isinstance(message, AssistantMessage):
                if message.content is not None and self._termination_message in message.content:
                    return Terminated(TerminationReason.TERMINATION_MESSAGE, "Termination message received.")

        return NotTerminated()

    def reset(self) -> None:
        self._turns = 0