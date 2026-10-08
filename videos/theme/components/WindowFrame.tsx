import {Circle, Node, Rect, Txt, type RectProps} from '@revideo/2d';
import {palette, trafficLights} from '../palette';
import {fonts, fontSize, radius} from '../style';
import {Card} from './Card';

export interface WindowFrameProps extends RectProps {
  width: number;
  height: number;
  /** Centered window title; ignored when an address is given. */
  title?: string;
  /** Shows a browser address field instead of a title. */
  address?: string;
  bodyFill?: string;
}

export const titleBarHeight = 56;

/** macOS window: traffic lights, a title or address field, and a body that clips to the rounded bottom corners. */
export class WindowFrame extends Card {
  /** Container centered in the body area; add content here. */
  public readonly body: Node;
  public readonly bodyWidth: number;
  public readonly bodyHeight: number;

  public constructor(props: WindowFrameProps) {
    const {title, address, bodyFill, ...rest} = props;
    super(rest);
    this.bodyWidth = props.width;
    this.bodyHeight = props.height - titleBarHeight;
    const barY = -props.height / 2 + titleBarHeight / 2;

    const corner = radius.card;
    this.body = new Node({});
    this.add(
      <Rect y={titleBarHeight / 2} width={this.bodyWidth} height={this.bodyHeight} fill={bodyFill ?? palette.card}
        radius={[0, 0, corner, corner]} smoothCorners clip>
        {this.body}
      </Rect>,
    );
    this.add(
      <Rect layout offset={[-1, 0]} x={-props.width / 2 + 22} y={barY} gap={9}>
        {trafficLights.map(color => <Circle size={14} fill={color} />)}
      </Rect>,
    );
    if (address !== undefined) {
      this.add(
        <Rect layout y={barY} width={Math.min(560, props.width * 0.5)} height={34} radius={radius.small}
          fill={palette.elevated} justifyContent={'center'} alignItems={'center'}>
          <Txt fontFamily={fonts.text} fontWeight={500} fontSize={20} fill={palette.secondaryText} text={address} />
        </Rect>,
      );
    } else if (title !== undefined) {
      this.add(
        <Txt y={barY} fontFamily={fonts.text} fontWeight={600} fontSize={fontSize.detail - 2} fill={palette.secondaryText} text={title} />,
      );
    }
  }
}
