import {Rect, type RectProps} from '@revideo/2d';
import {palette} from '../palette';
import {radius, shadow} from '../style';

/** Rounded surface with a soft shadow and no outline. */
export class Card extends Rect {
  public constructor(props: RectProps) {
    const {fill, ...rest} = props;
    super({radius: radius.card, smoothCorners: true, ...rest});
    // The shadow lives on a background child: a shadowed node is drawn through a cache that misses
    // changes to its children's paint properties, such as an output line fading in.
    this.insert(
      <Rect layout={false} width={() => this.width()} height={() => this.height()} radius={() => this.radius()}
        smoothCorners={() => this.smoothCorners()} fill={fill ?? palette.card} {...shadow} />,
      0,
    );
  }
}
